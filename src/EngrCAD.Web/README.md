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

`ViewportFrame.Build(instances, camera, bounds, aspect, furniture, style, pixelScale)` is
the browser's counterpart to `ViewportControl.OnOpenGlRender` and
`OffscreenRenderer.Draw` — and it is a **pure function**, so unlike either of them it can
be asserted directly. That is not a stylistic preference: those two drifted precisely
because the only way to compare them was to look at pixels. `EngrCAD.Web.Tests` pins the
draw order, the clear colour, the furniture ranges, the per-instance matrices, the
per-mode passes, the translucent sort, the blend and depth-mask state, and the neutral
shader state — all as values.

Three decisions there are load-bearing and easy to "fix" wrongly:

- **Fills do not cull.** Both desktop passes leave face culling off, because a section
  plane exposes a solid's interior as *backfaces*, which the shared fragment shader
  shades as cut material via `gl_FrontFacing`. Culling would look fine today and break
  the section rung silently.
- **`uSectionCount` is never sent.** It is an `int` uniform, and the interop marshals
  every JSON number through `uniform1f`, which GL rejects on an int. The clip rule
  short-circuits on `uSectionEnabled` and an unset int uniform is already 0, so the
  neutral state must say nothing about it. A test asserts the absence.
- **Translucent fills carry no polygon offset.** Opaque fills are pushed back so the
  edge overlay wins the depth test; translucent fills write no depth at all, so there is
  nothing for their edges to z-fight with — and the desktop disables it here too.

## Display modes and the view style

The global `ViewStyle` (Points / Wireframe / Shaded / ShadedWithEdges) and each part's
own `Part.DisplayMode` (Shaded / Wireframe / Translucent) meet in **one** place:
`RenderModes.Resolve` in `EngrCAD.Viewer.Core`, the same call the window and the
offscreen pass make. An explicit non-default part mode wins; parts left at the default
follow the style. Nothing in this project restates that rule — the unit tests read the
expectation *from* `Resolve` rather than repeating it, because a second copy of a
precedence rule agrees with a broken implementation just as happily as with a correct one.

The pass order is the desktop's, transcribed: background, furniture, opaque fills, the
whole line overlay, points, then translucency last — blended fills back-to-front by
distance from the eye (`RenderModes.SortBackToFront`) with depth writes off, then their
feature edges opaque on top. Fills and edges are separate passes so one part's fill
cannot hide another part's edges.

Three things worth knowing:

- **All three buffers go up for every part**, whatever the current style: the display
  mesh, `Part.GetFeatureEdges()` and `WireframeEdges.Extract()`. The style is a dropdown,
  so switching it must be a redraw, not a re-upload — `ViewStyle` and
  `RefreshDisplayModesAsync()` touch no GPU memory. (The one-shot offscreen pass uploads
  only what its mode needs, because it has no dropdown.) The desktop window makes the
  same trade in `UploadShared`.
- **Feature edges are not derived here.** `Part.GetFeatureEdges()` already exists, is
  cached, and for a B-Rep-backed part reads the *actual* B-Rep edges sampled at display
  resolution rather than mesh dihedrals — which is why a bore rim stays a smooth circle
  at any tessellation. This project plumbs it through the existing line program.
- **Point sprites need no capability enable.** `gl_PointSize` is gated behind
  `GL_PROGRAM_POINT_SIZE` on desktop GL — `ViewportControl` enables it and skips the
  enable under GLES, where the cap does not exist. **WebGL2 *is* OpenGL ES 3.0**, so the
  shared point shader ports unchanged and there is nothing to turn on. The size itself
  does need scaling: `gl_PointSize` is measured in *framebuffer* pixels, so
  `ViewportFrame` multiplies by the device pixel ratio exactly as the window multiplies
  by its DPI scaling and the offscreen pass by its supersample factor.

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
and `readPixels` works.

### Per mode, against the desktop as the reference

A single pixel count proves one mode drew something and says nothing about whether the
modes *differ*, so the `?report` check now walks every mode and reports a count for each,
using the **same classifiers `ViewStyleRenderTests` runs against `OffscreenRenderer`** —
which makes the desktop the reference rather than a second opinion. Demo scene (steel
flange over an amber backplate), furniture off, ambient occlusion off both sides, a
673x420 drawing buffer, WARP:

| | WebGL2 (browser) | `OffscreenRenderer` | |
| --- | --- | --- | --- |
| lit (all channels > 90) | 33 377 | 33 944 | |
| bright steel, Shaded | 35 980 | 36 043 | |
| bright steel, ShadedWithEdges | 35 183 | 35 692 | |
| pixels the edge overlay *darkened* | 1 410 | 554 | line width, see below |
| pixels it *brightened* | **0** | **0** | an overlay must only darken |
| bright steel, Wireframe | 26 228 | 19 980 | line width |
| bright steel, Points | 5 306 | 5 714 | |
| warm pixels, flange opaque | 403 | 449 | backplate seen down the bore |
| warm pixels, flange translucent | **20 045** | **21 085** | 49.7x / 47.0x |
| bright steel, flange translucent | 220 | 238 | a 0.4 blend leaves the class |

Fills, points and translucency agree within 2-10%. The two rows that differ more are both
**line** measures, and both have the same cause: the desktop renders at 2x and
box-downsamples, so a 1-pixel line contributes a quarter of a final pixel and falls below
an absolute brightness threshold, while the browser draws 1-pixel lines at final
resolution. That is the documented supersampling difference, not a difference in what is
drawn.

The relationships hold identically on both: shaded > wireframe > points; the edge overlay
darkens and never brightens; a translucent part drops out of the bright-fill class and
lets what is behind it through. And **61 425 pixels change** when the camera is orbited
through `CameraMath.DragOrbit` — a viewport that drew once and then ignored the camera
would pass every check above and fail that one.

One measurement lesson from building this: the classifier has to survive the blend. At
0.4 alpha under steel (whose blue is 0.84) a `Palette.Coral` part behind the flange
arrives at `r - b = +8`, indistinguishable from noise, and the reveal measured 1 478
pixels instead of 21 083. The hidden part is amber for that reason, and the number to
trust is the *ratio* to the opaque case, not the absolute.

### Traps in the headless loop

Four, all paid for:

- `--dump-dom` and `--screenshot` fire at load, so the browser needs
  `--virtual-time-budget` to reach the end of the work — and a budget too small dumps
  the boot placeholder with no error at all.
- **Under virtual time the clock does not advance during synchronous computation**, so
  every in-page *timing* reads 0 ms. Pixel counts are fine; they are not clocks.
- **A self-check that stalls reports nothing, which looks exactly like a check that was
  never reached.** `Console.WriteLine` from Blazor WASM reaches the browser console and
  `--enable-logging=stderr` captures it, so the report traces each stage; that is how the
  stall below was found in one run instead of by bisecting a publish.
- The viewport raises `OnRendered` for the **empty** scene it starts with (a `Furniture`
  change is a reload, and `?report` turns furniture off), so a host's rendered-handler
  must guard on its own model being built. Without that guard the check ran against a
  blank canvas, consumed its run-once flag, and the real one never fired.

Synthetic input: unlike the desktop toolkit, Blazor **does** receive `dispatchEvent`-ed
`PointerEvent`s, which is verifiable without pixels because the handler's state reaches
the DOM — the canvas cursor switches to `grabbing` on `pointerdown` and back on
`pointerup`.

## Status

Kernel-in-the-browser, the WebGL2 interop layer, the scene-to-frame layer, the orbit
camera, feature edges, per-part display modes and the global view style are in place, and
the demo draws real geometry with a style and a mode selector. Still to build: model
tree, picking, section planes and their isolines, the view cube, and annotations. The
parity ladder is in `todo.md`.

Notes for whoever takes the next rung:

- `SectionClip` is already shared and waiting; the frame builder deliberately does **not**
  half-apply it, because a mode resolved and then ignored looks like support and is not.
  `uSectionEnabled` is 0 in every frame today.
- There is no ambient-occlusion bake in the browser. `uAmbientOcclusion` is 0, which
  makes the factor exactly 1.0 and *is* the AO-off shading rather than an approximation
  of it — the same property that lets the desktop stream bakes in behind a live scene.
- Frame-constant uniforms ride on `FrameDescription.Shared` so they travel once instead
  of once per instance. For a scene of any size that is most of the interop payload, and
  it is the first place to look if a large assembly feels heavy during a drag.
- Every part uploads its mesh, feature edges *and* wire edges. If a very large assembly
  ever makes that memory matter, upload lazily per mode — do not go back to
  upload-what-the-current-style-needs, which puts a re-upload behind a dropdown.
