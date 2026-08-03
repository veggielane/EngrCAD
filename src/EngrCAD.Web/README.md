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
  sectioning silently.
- **`uSectionCount` travels as a typed marker, never a plain number.** It is an `int`
  uniform, and the interop marshals every JSON number through `uniform1f`, which GL
  rejects on an int with no visible error. `IntUniform` serializes as `{"int": n}` and
  dispatches through `uniform1i`; `Vec4ArrayUniform` (`{"vec4": [...]}` →
  `uniform4fv`) exists because four packed section planes are exactly 16 floats, which
  the shape dispatch would otherwise send as a mat4. WHICH uniforms need which type
  stays a C# decision — the JS only dispatches on the marker's shape.
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

**What a part's upload CONTAINS is shared; when it is released is not.** Everything
`UploadPartAsync` computes before its first interop call is one
`PartUploads.Build(part, PartUploadRequest.All)` in `EngrCAD.Viewer.Core` — the flat
`RenderMesh`, the field colour and displacement buffers, the feature-edge and wireframe
segments, the pick BVH — the *same call* the desktop window and the offscreen pass make,
so the browser cannot upload different floats for one part. `All` is the window's policy
too, and for the window's reason: the view style is a dropdown, so every piece goes up
whatever is currently drawn. No occlusion source is supplied, because there is no bake
here; the attribute's constant-when-absent rule makes that exactly the flat-lit shading
rather than a special case. The **cache** is deliberately not shared — releasing on tab
switch is this client's own lifetime, where the window releases on GL deinit.

Verified the way this front end can be: two clean publishes, before and after the
extraction, driven through `?report` in the same headless Edge sitting produced a
**character-identical beacon** — every pixel relationship in it, from `tris=1560` and
`bodyWhole=32374 → bodySectioned=20593` down to `hoverShifted=0`. The one field that moved
is `tabSwitchMs`, which is a clock reading under `--virtual-time-budget` and therefore
meaningless in a `--dump-dom` run (see below).

### This client does NOT use `TabMeshLoader`, and the reason is not Avalonia

`TabMeshLoader` now lives in `EngrCAD.Viewer.Core` (it is genuinely Avalonia-free and
headlessly unit-tested), so using it here looks obvious. It is the wrong call: it is
**thread-model**-bound, not UI-bound.
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

## Section planes, and the isolines on the cut

`EngrCadViewport` carries the desktop surface — `SectionEnabled`/`SectionAxis`/
`SectionOffset` (null = centred in the scene bounds), `SetSectionAsync` (an axis change
re-centres, the desktop rule), `NudgeSectionAsync` (2% of the scene extent per step) —
and the SceneView toolbar drives it exactly as the desktop toolbar does: toggle, axis
cycler, nudge. **Nothing about the cut itself is decided in this project.** The clip is
the shared shader rule (`ViewerShaders.SectionClip`); the frame only carries the
uniforms, packed exactly as the desktop's `SectionUniforms.Write` packs them (via the
`Vec4ArrayUniform` marker — see the trio above). Scene furniture, the cube, annotations
and `Part.ClippedBySection`-exempt parts carry a per-draw `uSectionEnabled = 0`
override, the browser's version of the desktop's per-draw-group `SetEnabled(false)` —
so a fastener stands whole inside a cutaway, in the render AND in the pick, because the
pick goes through the same `SectionClip.Hides` the shaders state (`ScenePick` applies
it; this project passes three arguments and restates nothing).

The SDF isolines ride the shared `SectionContours` (moved to `Viewer.Core` for exactly
this): the same cached `Part.TryGetSdf` route, the same marching squares, the same lift
below the plane that keeps the fragment discard from eating the lines, the same
1-2-5 level spacing, and the same three family colours — recomputed on a
section/visibility/load change, never per frame, and drawn signed-families-first so the
gold d = 0 cross-section wins overdraw.

**Multi-plane isolines have full desktop parity**: `RebuildContoursAsync` runs one
`SectionContours.Build` per ACTIVE plane (the desktop worker's exact loop), each
`ViewportContours` entry carries the plane it was built for, and the frame clips each
plane's contours by its **sibling** planes — `SectionClip.Siblings`, always applied
with Union, so a quarter cut shows each cut face's isolines only where that face is
actually exposed instead of across the plane's full extent. A single plane has no
siblings and its draws opt out of the clip entirely (the incumbent behaviour, bit for
bit); a plane whose own build found no SDF-routed parts keeps an EMPTY entry, because
its plane still bounds its siblings' cut faces. The sibling set comes from the planes
the drawn geometry was BUILT for — the desktop renderer's self-consistency rule — and
the per-draw override packs the sibling planes exactly as the shared uniforms are
packed, typed markers included, since the interop lets a call's own uniforms win.
`SetSectionPlanesAsync(planes, combine)` is the direct-call path for quarter/octant
cuts, and `SceneBounds` is what a host centres the planes with.

## Poses: exploded views, animation playback, and the measure tool

Three affordances, one mechanism. `Explode` (0..1), `Animation` + `AnimationTime`, and
the SceneView's transport row all end at the same place: **new matrices over buffers
that are already on the GPU**. That works because of the one rule the exploded view and
the animation both keep — the instance COUNT and ORDER never depend on the factor or on
t — so this is the browser's `SetInstancePoses`, and picking follows for free because
the pick instances take the same matrices.

The matching rule is `ViewportFrame.PoseByPath`, a **pure function** and the whole of
the content: poses are matched by occurrence PATH, not by index, so a whole-scene pose
track carrying instances this tab does not draw is ignored and an instance the track
says nothing about keeps its DOCUMENT pose. Index matching gets both wrong the moment a
tab shows a subset, and the symptom — a part wearing its neighbour's transform — looks
like a modelling error rather than a viewer bug. It is asserted as values
(`PoseFrameTests`), including that the explode SLIDER and an `ExplodeTrack` produce
identical matrices at factor 1, which is what stops the transport and the slider being
two different exploded views.

Playback itself is not implemented here at all: `AnimationPlayback` from
`EngrCAD.Viewer.Core` is the state machine (play/pause/loop/seek, `Advance` wrapping the
overshoot so speed is independent of the tick quantum), and this project supplies a
timer and three widgets. The timer advances by REAL elapsed time rather than by a fixed
step, so a throttled background tab resumes at the right position instead of running
slow. An animation with a camera track drives the camera too, through the sample's own
`CameraState`.

The **measure tool** is the pick raycast plus two fields: two clicks in measure mode
make a `LinearDimension` between the picked world points, resolved and drawn through the
shared `AnnotationGeometry`. It shows **whatever the Annot toggle says**, because it is
the answer to a question the user just asked rather than documentation attached to a
part — the desktop's rule, restated because the two front ends decide it separately.

Debug modifiers reach this client now too: `ResolveInstances` goes through
`DebugFilter.Shown`, so `Part.Hidden` never renders (and cannot influence framing), an
active `Part.Isolated` shows only isolated parts, and `Ghost` renders translucent via
`Part.EffectiveDisplayMode` as it already did. With no flags set the filter is the
identity, which is why adding it moved no pixels.

## The view cube, and annotations

Both rungs are thin: their pure halves live in `EngrCAD.Viewer.Core` and this project
uploads and routes. The cube's fills/edges/labels are `ViewCubeGeometry`'s arrays — the
same floats the desktop widget uploads — drawn last into a top-right sub-viewport with
the depth buffer cleared first (`DrawCall.Viewport`/`ClearDepth`, applied by the JS
without policy); clicks resolve through `ViewCubeMath.TryHit`/`PoseFor` (the pose table
the desktop toolbar shares, so "Front" cannot mean two things), a click inside the
region is CLAIMED so parts behind the widget are never picked through it, a drag that
started on the cube rotate-snaps to `NearestStandardDirection` on release, and the
transition is `ViewCubeAnimation` — the 250 ms smoothstep constant, taken rather than
re-typed. `ViewCubeClickAsync`/`SnapToNearestViewAsync` are public for the reason the
desktop's `ViewCubeClick` is.

Annotations resolve per instance through `Part.TryResolveAnnotations` at load (cached
lowering; a broken selector becomes a status message) and build through
`AnnotationGeometry` — billboarded, screen-constant, rebuilt only when the
`AnnotationCamera` VALUE, the visibility set or the depth mode changes, the desktop
layer's exact rebuild key — then draw in the shared colour, never section-clipped.
Hiding a part hides its annotations; the toolbar's Annot toggle matches the desktop's.

`AnnotationDepth` (the `AnnotationDepth` parameter, default `AlwaysOnTop`) reaches the
frame as **draw calls rather than as a flag**: `Occluded` emits three over ONE upload —
the line-work range at `lequal` in `AnnotationGeometry.Color`, the same range at
`greater` in `HiddenColor`, then the text range depth-off — which is
`AnnotationLayer.DrawBatches` transcribed, and the reason `DrawCall` gained a
`depthFunc`. It travels as a **name** (`"lequal"`/`"greater"`), not a number: WHICH
comparison a draw wants is a .NET decision and the GL enum values are the browser's, so
no numeric constant is duplicated across the boundary. The split between the two ranges
comes from `AnnotationGeometry.Build`'s optional text list, which the component
concatenates before uploading and reports as `ViewportAnnotations.LineWorkVertexCount`.

## Simulation results (colour maps, legend, deformed shape)

Thin for the same reason: `FieldRendering.TryBuild` (EngrCAD.Viewer.Core) produces the
colour floats and the displacement attributes both desktop passes upload, and this
project only uploads them and says which draws carry `uFieldColor` and `uDeformScale`.

**Both are vertex attributes** — `aFieldColor` at slot 3 and the deformation block at
4–7, all bound in `createProgram` beside `aOcclusion` and carrying the same
constant-when-absent rule: `uploadMesh` with no such bytes disables those arrays and sets
the context constants (white, and zero), the frame's shared `uFieldColor` and
`uDeformScale` are 0, and only an affected instance's own fill overrides them. That
neutral default is what makes a part with no results produce identical pixels. The
deformation arrives as ONE buffer of four interleaved vec3s per vertex, so it is four
attribute pointers over one upload rather than four buffers.

**Animating a result is therefore one uniform per frame in the browser too**: the geometry
uploaded is always the undeformed mesh, `ViewportFrame.Build` takes a `deformFactor`, and
an `Animation`'s `DeformationTrack` supplies it — the same mechanism the explode slider and
the pose track already use, reaching the front end as a number instead of matrices.
`DeformFrameTests` pins the claim as values: changing the factor changes exactly one
uniform on exactly one draw and leaves every geometry key, draw and other uniform
identical, which a pixel test could not distinguish from a re-upload.

The undeformed shape goes up under its own `.ghost` key (it must look like the undeformed
part, so it keeps that part's face normals) and draws blended with depth writes off after
every fill; a part carrying a displacement uploads no feature edges at any factor, since
they describe geometry that has moved and deciding it per frame would make the draw list
depend on `t`. Picking follows the part's own exaggeration and deliberately **not** an
animation's factor — the BVH is built once over `FieldRendering.PickShape`, because a
spatial index cannot be a uniform.

The legend is `FieldLegend`'s geometry uploaded under two keys and drawn one call per
band (each needs its own colour, exactly as the cube's faces are drawn), with its own
pixel-coordinate projection and the depth test off, between the annotations and the
cube. It is rebuilt only when the resolved display, the canvas size or the effective
exaggeration changes — value equality as the key, the annotation overlay's rule; the
factor is in that key because the title states the number.

## Properties panel and BOM

`PartFacts.For(row, instance)` is the desktop properties panel as a pure function
(Name/Kind/Display + Faces/Closed/Volume/Area/Size/Position), **gated on
`Part.HasMesh`** exactly as the desktop gates it — the panel must never mesh a part the
loader is still working on. The BOM button shows `Bom.For(tab)` as the same monospace
`ToText` the desktop windows, with `ToCsv()` as a data-URI download link (the browser's
"drop a CSV beside the window").

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

**The forward dependency paid off.** `ScenePick` was built taking the plane set, and
each `PickInstance` carried the part's `ClippedBySection` a rung early — so when
sections landed here, picking-honours-the-cut was passing three more arguments in
`EngrCadViewport.PickAt`, exactly as planned, and no second clip rule was ever written.

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
in, and a feature nothing can reach is a feature nothing checks. `?example=<id>` shows one
documentation example instead of the flange demo (below), and composes with both the
others.

## Live documentation examples

`LiveExample.RunAsync(byte[])` loads one of the assemblies `EngrCAD.DocsGen` emitted for a
documentation snippet and returns the `Scene` it built, plus the render inputs the snippet
declared (`camera`, `sectionPlanes`, `sectionCombine`, `explode`, `shading`). That is what
puts a **Run it in your browser** button under every example screenshot on the docs site:
the reader gets the model built in their own tab rather than a mesh baked at docs-build
time, and the committed PNG stays the poster because the runtime is megabytes and the PNGs
are the build's own regression oracle.

**Why an assembly rather than source.** A browser cannot cheaply compile C# — Roslyn in
the payload is several megabytes — but the documentation build compiles every snippet
anyway, so it emits what it compiled. The half that decides *which* examples are offered
lives there (`tools/EngrCAD.DocsGen/README.md`): the browser's reference set is the rule,
so the C# compiler answers it.

**The submission ABI is the load-bearing detail, and nothing on either side checks it.** A
snippet is a C# *script*; Roslyn compiles one into a type with a static
`<Factory>(object[])` returning `Task<object>`, whose array is the submission state — slot
0 the globals, slot 1 the instance the factory constructs. Every top-level variable of the
script is a **field on that instance**, which is exactly how the docs harness reads `scene`
back out of a `ScriptState`; this reads the same fields without needing Roslyn to do it,
and finds the factory by SHAPE rather than by the `Submission#0` type name. The round trip
— emit here, load there, compare the geometry — is pinned by
`tests/EngrCAD.DocsGen.Tests`.

Two findings from building it:

- **A globals-less script cannot see `object`'s statics.** Roslyn puts a script's globals
  type's members in scope, inherited ones included, so a submission compiled with no
  globals at all fails on a bare `ReferenceEquals(a, b)` — legal in every ordinary C#
  class, and used by `chamfer-fillet.md`. Handing over `object` as the globals type
  restores exactly that scope and nothing else: it adds no assembly reference and the
  docs-only `Scratch` still does not exist, so the one snippet needing it is still refused
  for the reason it should be.
- **The trimmer is why it works, and it works by default.** A dynamically loaded example
  calls the kernel by reflection, which the trimmer cannot see. Blazor WebAssembly trims in
  `partial` mode, leaving assemblies not marked `IsTrimmable` alone, and none of ours is —
  but that is a default, so the demo lists the kernel assemblies as `TrimmerRootAssembly`
  to say it out loud. Measured, that costs nothing: the published payload is the same size
  with and without.

Measured time to a finished frame, warm runtime, headless Edge on win-x64, out of the
`?example=<id>&report` beacon: extrusion **369 ms**, sheet metal bracket **461 ms**,
four-bar linkage **604 ms**, B-Rep thread **1 075 ms**, sectioned housing with isolines
**1 093 ms**, helical gear **6 712 ms**. A refused id beacons its error rather than going
quiet — a self-check that reports failure and success the same way is one that cannot tell
a missing example from a slow one.

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
model tree with subtree visibility, client-side picking, two-way selection sync, the
hover highlight, **section planes with picking parity and SDF isolines on the cut, the
view cube, 3D annotations, the toolbar, the properties panel and the BOM button** are in
place, and so are **the measure tool, exploded views, animation playback, the
multi-plane section surface — now including per-plane, sibling-clipped SDF isolines —
and debug-modifier parity**. Still to build: construction-tree rows and their rollback
previews. The parity ladder is in `todo.md`.

The `?report` self-check covers the new rungs as pixel relationships (cube and
annotations start OFF under `?report`, like the furniture, because their near-white
strokes would land in the "steel" class and skew the comparative counts): sectioning at
the scene centre removed **32 374 → 20 593** body pixels with **676** gold d = 0
isoline pixels on the cut; toggling the flange's dimension changed **34 317** pixels (this
one was re-measured on win-x64 and had gone stale at 786 — see the note below); the
cube lit **4 770** pixels in its corner region, claimed a click on the region
(`cubeClaimed=1`) and landed the camera EXACTLY on a shared-pose-table orientation
(`cubeSnapped=1` — checkable without naming the face, because the animation's final
step returns the target). The drawing buffer is 693×393 now that the toolbar takes a
row, so absolute counts are not comparable with the older tables above — the
relationships are the point, and all of the pre-toolbar checks reproduce.

**One of those numbers had gone stale, and finding it is what a control is for.** The
live-examples work needed to show it had moved nothing, so the whole beacon was captured
against the commit before it and against the commit after: **all 44 fields identical**.
The same pair of runs showed `annotationPixels` reading **34 317** where this file said
786 — an order of magnitude more than the dimension's own strokes, and more than the whole
model's silhouette (32 374), so the toggle is repainting the frame rather than adding an
overlay to it. It is *not* the live-examples change (identical on both sides) and the
buffer is the same 693×393, so the comparison is like for like; the likeliest suspect is
the occlusion-aware annotation work, which gave the overlay two depth passes. Filed in
`todo.md` for whoever has that context — the check still fires, it is just no longer
measuring only what its name says.

Notes for whoever takes the next rung:

- There is no ambient-occlusion bake in the browser. `uAmbientOcclusion` is 0, which
  makes the factor exactly 1.0 and *is* the AO-off shading rather than an approximation
  of it — the same property that lets the desktop stream bakes in behind a live scene.
- `EngrCadViewport.SectionPlanes` + `SectionCombine` carry quarter and octant cuts
  through to the shaders, to picking (`ScenePick` takes the same combine) AND to the
  isoline overlay (per-plane builds, sibling-clipped — see the section above), clamped
  to `ViewerShaders.MaxSectionPlanes`; the *toolbar* still drives the one-plane
  axis/offset spelling, while the desktop toolbar also has a plane-count cycler —
  a browser toolbar affordance for the plane set is the remaining gap.
- `EngrCadViewport.Shading` / `SetShadingAsync` select the analytic matcap
  (`ShadingStyle` — Lit / Clay / Metal), one `uMatcap` int uniform in the shared
  frame; the shader is `ViewerShaders.MeshFragment`, the same string the desktop
  compiles, so the three front ends cannot light a fill differently.
- Frame-constant uniforms ride on `FrameDescription.Shared` so they travel once instead
  of once per instance. For a scene of any size that is most of the interop payload, and
  it is the first place to look if a large assembly feels heavy during a drag.
- Every part uploads its mesh, feature edges *and* wire edges, and builds a pick BVH over
  the same `RenderMesh`. If a very large assembly ever makes that memory matter, upload
  lazily per mode — do not go back to upload-what-the-current-style-needs, which puts a
  re-upload behind a dropdown. The BVH is not optional: without it a click is a linear
  scan of every triangle in the scene.
