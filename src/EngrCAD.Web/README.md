# EngrCAD.Web

The web viewer: a Blazor WebAssembly component that renders EngrCAD scenes with WebGL2,
with **the whole kernel running in the browser**. No server, no round trip — a page
loads the geometry kernel, builds the model client-side, and draws it.

`samples/EngrCAD.WebDemo` is the host app.

Two components:

- **`EngrCadViewport`** — the WebGL2 canvas. Owns the GL context, the camera and the
  pointer bindings, and nothing about what a frame looks like.
- **`EngrCadSceneView`** — the chrome around it: tab strip, model tree with visibility,
  status line. The desktop split, for the desktop reason (`ViewportControl` owns GL;
  `SceneHost` owns the document's structure).

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

`ViewportFrame.Build(instances, camera, bounds, aspect, furniture, style, pixelScale,
selected, hovered)` is the browser's counterpart to `ViewportControl.OnOpenGlRender` and
`OffscreenRenderer.Draw` — and it is a **pure function**, so unlike either of them it can
be asserted directly. That is not a stylistic preference: those two drifted precisely
because the only way to compare them was to look at pixels. `EngrCAD.Web.Tests` pins the
draw order, the clear colour, the furniture ranges, the per-instance matrices, the
per-mode passes, the translucent sort, the blend and depth-mask state, and the neutral
shader state — all as values.

**Visibility, selection and hover are arguments to it**, not state it reaches for.
`ViewportInstance.Visible` decides whether an instance contributes draws; `selected` and
`hovered` decide what the draws it does contribute are told. So "hiding a part removes
its edge overlay too" and "selecting one tints its fill and golds its edges without
moving the silhouette" are assertions over a returned value.

The one property everything downstream leans on: **a hidden instance keeps its index.**
The tree, the selection, and the pick list all address instances by the same number, so a
checkbox can never make one of them point at different geometry from another.

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

## Tabs, the tree, and what they share with the desktop

`Scene` groups parts into named `Tab`s, and the viewport shows **one at a time** — the
desktop's model, and the reason a tab is meshed when it is first *viewed* rather than up
front. `EngrCadSceneView` renders the strip; `EngrCadViewport.TabIndex` selects.

The tree itself is a value: **`SceneTree.Build(tab, failed)`** walks the tab exactly the
way `Tab.Instances()` does (loose parts first, then each assembly depth-first) and hands
back indented-flat rows carrying the occurrence path, the depth, the enclosing assembly
rows, and the **instance index the viewport draws that occurrence at**. That last number
is the whole point, and it is tested against `Tab.Instances()` itself rather than against
a second hand-written walk.

Three rules it keeps, all the desktop's:

- **Effective visibility is own AND every ancestor.** Unchecking an assembly hides its
  subtree without touching the children's own state, so re-checking restores exactly what
  was showing. `EffectiveVisibility(hidden)` returns the `bool[]` the viewport consumes,
  indexed by instance.
- **Unchecked rows are remembered by key, not by index** (`Kind:path`), because the tree
  is rebuilt whenever a part fails or a tab is revisited.
- **A part that failed to mesh takes no instance index and does not advance the counter.**
  It has no instance in the viewport, so every row after it would otherwise address the
  wrong geometry — silently hiding, selecting and highlighting the wrong part. The
  viewport raises `PartFailed` for exactly this reason: the host must rebuild rather than
  keep indices that moved.

**Uploads survive a tab switch.** They are keyed by `Part` *reference*, and a load
releases only the parts the new tab does not use — so a part shown in two tabs is
uploaded once and a revisit costs no GPU work at all. Measured in the demo below: 19 ms
to switch to a tab placing the same two parts four times, against ~1 670 ms to build the
model in the first place.

### `TabMeshLoader` was NOT moved to `Viewer.Core`, and the reason is not Avalonia

The desktop's `TabMeshLoader` is genuinely Avalonia-free and headlessly unit-tested, so
moving it looks obvious. It is the wrong call: it is **thread-model**-bound, not UI-bound.
Its whole shape — `Task.Run` for the work, a `post` delegate back to the UI thread, a
`Volatile` generation token read across threads — assumes two threads. WebAssembly has
one. `Task.Run` there runs the loop to completion on the same thread with no chance to
paint, which loses the growing-prefix property the class exists for, and the interleaved
uploads are `await`ed JS interop calls that cannot happen inside a synchronous worker at
all.

So the browser keeps its own loader inside `EngrCadViewport.LoadAsync`, with the same
three rules and cooperative yielding in place of the threading:

- publish a **growing prefix**, so the viewport is orbitable while the rest computes;
- re-check a **generation token after every await** that precedes a mutation, so a slider
  moved mid-load cannot let a stale pass file a key the new one released;
- **name a part that throws** rather than swallowing it — it is dropped, reported through
  `PartFailed`, and the rest of the tab still loads.

`Task.Delay(1)`, not `Task.Yield()`: yielding posts the continuation straight back onto
the same loop, which need not paint first.

## Picking, and selection sync

Picking is **client-side** — the kernel is in the browser, so a click is a local BVH
raycast, never a round trip — and the maths is `ScenePick` in `EngrCAD.Viewer.Core`, the
same call `ViewportControl.HitTest` makes. It is deliberately **not** in
`engrcad-gl.js`: a ray unprojected in JavaScript is exactly the kind of thing that
silently disagrees with the camera the frame was drawn with, and the disagreement is
invisible until someone clicks near an edge.

`PickMesh.Build` runs beside each part's upload over the *same* `RenderMesh`, so a click
lands on the triangle that was drawn. Instances share the mesh and the BVH and carry only
their own matrix, exactly as the GPU buffers do.

The surface mirrors the desktop's: `Selected`, `SelectAsync(index)` (programmatic — does
**not** raise `SelectionChanged`, so a tree click cannot echo back into the tree),
`SetVisibleAsync`/`SetVisibilityAsync`, `FitAsync`, `PickAtAsync(x, y)` (pick without
selecting), `HoverAtAsync(x, y)`, and the `SelectionChanged` callback which means "the
user clicked in the viewport". Click-vs-drag uses the desktop's threshold: a release
within 4 pixels of the press picks, anything farther was a drag.

Hover fell out of the pick path cheaply and is in: pointer moves re-pick through
`HoverThrottle` (4 pixels of travel, also moved to `Viewer.Core`), and a frame is redrawn
only when the hovered index actually changes.

**One forward dependency, designed in rather than deferred.** Section planes are the next
rung, and on the desktop picking honours them — `SectionClip.Hides` holds the shaders'
discard rule in one place so a click cannot select through a cut-away corner. `ScenePick`
already takes the plane set and each `PickInstance` already carries the part's
`ClippedBySection`, so turning sections on here is passing three more arguments, not
writing a second clip rule. The desktop path proves it works today
(`ScenePickTests` picks through a sectioned box and lands on the interior the cut
exposed).

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

`?embed` drops the page heading and footer for that iframe; `?report` runs the timing and
pixel self-check described above; `?tab=N` deep-links a tab, which exists so a headless
run can screenshot the Assembly tab's tree — without a real click there is no other way
in, and a feature nothing can reach is a feature nothing checks.

## Proving it drew, headlessly

**A black canvas throws nothing**, so "no errors in the console" is not evidence that
anything rendered. `WebGlContext.CapturePixelsAsync` re-draws the last frame and reads it
back (the context has no `preserveDrawingBuffer`, so a separate interop call would find
the buffer already gone), and `CapturedPixels.CountBrighterThan` turns it into a number
.NET can assert. The demo's `?report` beacon carries that number out.

Headless Edge **does** have WebGL2 even with `--disable-gpu`: it falls back to ANGLE over
the D3D11 WARP renderer (`ANGLE (Microsoft, Microsoft Basic Render Driver, Direct3D11)`),
and `readPixels` works.

### Picking, visibility and the highlight, proved with pixels

The self-check does not ask the picker whether it picked. The two parts are
distinguishable by colour — steel flange, amber backplate — so **the rasterizer already
knows which part covers which pixel**: find a pixel of each class in the read-back frame,
pick there, and the answers must agree. A picker that always returned the first part, or
one whose ray was flipped in y, passes "something was picked" and fails this. Each hit is
then checked against a *second, independent* fact: the reported world point must lie
inside that part's own world bounds, which come from the kernel's geometry rather than
from the BVH the ray was traced against.

| | measured | means |
| --- | --- | --- |
| `pickSteel` | **0** | a steel pixel picks the flange (instance 0) |
| `pickWarm` | **1** | an amber pixel picks the backplate (instance 1) |
| `pickEmpty` | **-1** | a background pixel picks nothing |
| `pickOnFlange` / `pickOnBackplate` | **1** / **1** | the hit point lies inside the picked part's bounds |

Visibility and the highlight are the same idea — relationships between states of one
canvas, which is what survives a change of window size. "body" counts any bright channel
(the model's silhouette, whatever colour it is drawn in); "steel" counts bright *and*
bluish. Selection blends the fill 55% toward gold, so it must move the second and not the
first:

| | plain | flange hidden | both hidden | restored | flange selected | flange hovered |
| --- | --- | --- | --- | --- | --- | --- |
| body | 38 314 | **24 922** | **0** | **38 314** | 39 610 | 37 908 |
| steel | 36 410 | **0** | 0 | 36 410 | **0** | 36 410 |

Read across: hiding the flange removes 35% of the model's pixels and *all* of the steel
(it is the only steel part); hiding both leaves nothing; restoring gives back exactly the
original count. Selecting the flange takes it entirely out of the steel class while
leaving the silhouette alone — the +1 296 body pixels are the feature-edge overlay turning
gold, which agrees with the 1 445 pixels the same overlay was measured *darkening* two
rows up.

Hover had to be measured rather than asserted. Both states change nearly every flange
pixel by at least a rounding step, so a plain difference count cannot tell them apart; a
*threshold* can. Selection shifts red by 63/255 and hover by 22/255, so counting pixels
shifted by more than 40 gives **36 648 for selection and 0 for hover** — faint, by
construction and by measurement.

What this does *not* cover: the synthetic pointer event itself. The checks drive
`PickAtAsync`/`SelectAsync`/`HoverAtAsync`, which is everything except the few lines that
translate a `PointerEventArgs` into those calls.

### Per mode, against the desktop as the reference

A single pixel count proves one mode drew something and says nothing about whether the
modes *differ*, so the `?report` check walks every mode and reports a count for each,
using the **same classifiers `ViewStyleRenderTests` runs against `OffscreenRenderer`** —
which makes the desktop the reference rather than a second opinion. Demo scene (steel
flange over an amber backplate), furniture off, ambient occlusion off both sides, a
693x427 drawing buffer, WARP, both sides framed by `ViewportFrame.DefaultCamera`:

| | WebGL2 (browser) | `OffscreenRenderer` | |
| --- | --- | --- | --- |
| lit (all channels > 90) | 34 529 | 35 141 | |
| bright steel, Shaded | 37 197 | 37 282 | |
| bright steel, ShadedWithEdges | 36 410 | 36 929 | |
| pixels the edge overlay *darkened* | 1 445 | 551 | line width, see below |
| pixels it *brightened* | **0** | **0** | an overlay must only darken |
| bright steel, Wireframe | 27 012 | 20 652 | line width |
| bright steel, Points | 5 347 | 5 803 | |
| warm pixels, flange opaque | 410 | 467 | backplate seen down the bore |
| warm pixels, flange translucent | **20 746** | **21 777** | 50.6x / 46.6x |
| bright steel, flange translucent | 233 | 251 | a 0.4 blend leaves the class |

Fills, points and translucency agree within 2-8%. The two rows that differ more are both
**line** measures, and both have the same cause: the desktop renders at 2x and
box-downsamples, so a 1-pixel line contributes a quarter of a final pixel and falls below
an absolute brightness threshold, while the browser draws 1-pixel lines at final
resolution. That is the documented supersampling difference, not a difference in what is
drawn.

The relationships hold identically on both: shaded > wireframe > points; the edge overlay
darkens and never brightens; a translucent part drops out of the bright-fill class and
lets what is behind it through. And **63 474 pixels change** when the camera is orbited
through `CameraMath.DragOrbit` — a viewport that drew once and then ignored the camera
would pass every check above and fail that one.

(The buffer is 693x427 rather than the 673x420 of the previous rung because the demo now
wraps the canvas in `EngrCadSceneView`'s chrome. Both columns above were re-measured at
the new size; a table whose two halves came from different framings would compare
nothing.)

One measurement lesson from building this: the classifier has to survive the blend. At
0.4 alpha under steel (whose blue is 0.84) a `Palette.Coral` part behind the flange
arrives at `r - b = +8`, indistinguishable from noise, and the reveal measured 1 478
pixels instead of the ~21 000 the amber part gives. The hidden part is amber for that
reason, and the number to trust is the *ratio* to the opaque case, not the absolute.

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
`pointerup`. The pick, hover and selection APIs are public for the same reason the
desktop's `HoverAt`/`ViewCubeClick` are: a state only reachable through a real pointer
event is a state nothing checks.

## Status

Kernel-in-the-browser, the WebGL2 interop layer, the scene-to-frame layer, the orbit
camera, feature edges, per-part display modes, the global view style, the tab strip, the
model tree with subtree visibility, client-side picking, two-way selection sync and the
hover highlight are in place. Still to build: section planes and their isolines, the view
cube, construction-tree rows and their rollback previews, the properties panel, and
annotations. The parity ladder is in `todo.md`.

Notes for whoever takes the next rung:

- `SectionClip` is already shared and waiting; the frame builder deliberately does **not**
  half-apply it, because a mode resolved and then ignored looks like support and is not.
  `uSectionEnabled` is 0 in every frame today. **Picking is the part that is already
  ready**: `ScenePick.Nearest` takes the plane set and each `PickInstance` carries the
  part's `ClippedBySection`, so the section rung has to pass three arguments in
  `EngrCadViewport.PickAt` and add the uniforms to `ViewportFrame.Build` — not write a
  second clip rule.
- **Do not move `TabMeshLoader` into `Viewer.Core`** for this client's benefit; see the
  section above. It is Avalonia-free but thread-model-bound, and the browser needs the
  opposite shape.
- There is no ambient-occlusion bake in the browser. `uAmbientOcclusion` is 0, which
  makes the factor exactly 1.0 and *is* the AO-off shading rather than an approximation
  of it — the same property that lets the desktop stream bakes in behind a live scene.
- Frame-constant uniforms ride on `FrameDescription.Shared` so they travel once instead
  of once per instance. For a scene of any size that is most of the interop payload, and
  it is the first place to look if a large assembly feels heavy during a drag.
- Every part uploads its mesh, feature edges *and* wire edges, and builds a pick BVH over
  the same `RenderMesh`. If a very large assembly ever makes that memory matter, upload
  lazily per mode — do not go back to upload-what-the-current-style-needs, which puts a
  re-upload behind a dropdown. The BVH is not optional: without it a click is a linear
  scan of every triangle in the scene.
