# EngrCAD.Viewer.Core

The **UI-free render model**: everything both EngrCAD render paths agree on that does
not touch a GL binding. No Avalonia, no Silk.NET, no `System.Drawing` — this assembly
must load in a WebAssembly client. It references only kernel projects: `EngrCAD.Core`
(math structs), `EngrCAD.Mesh` (the half-edge walk `WireframeEdges` does),
`EngrCAD.Modeling` (`DisplayMode`, `Part`/`PartInstance` for the loader and the pure
annotation/isoline halves), `EngrCAD.Implicit` + `EngrCAD.Interop` (the section-isoline
extraction) and `EngrCAD.BRep` (`MeshFlavor`'s geometry inspection).

## The namespace is `EngrCAD.Viewer` on purpose — do NOT "tidy" it

The assembly is `EngrCAD.Viewer.Core`; **the namespace of every type in it is
`EngrCAD.Viewer`**. That mismatch is deliberate and load-bearing:

- `SectionPlane`, `SectionAxis`, `SectionCombine` and `ViewStyle` are **public API**.
  They appear in `EngrCadOptions`, in `EngrCad.RenderToImage(...)`, in the MCP server's
  screenshot tool, in docs-site snippets and in tests.
- Renaming the namespace would churn every one of those call sites, break every
  consumer's `using EngrCAD.Viewer;`, and buy the user exactly nothing. An assembly
  boundary is a packaging decision; a namespace is API.

So: a `using EngrCAD.Viewer;` resolves types from two assemblies, and that is fine.
Nothing in .NET requires a namespace to live in one assembly.

## Why the split exists

`RenderCore.cs` was created because the interactive `ViewportControl` and the headless
`OffscreenRenderer` had duplicated ~150 lines and **drifted silently** — the offscreen
pass gained a scene-scaled frustum the window never got, so large scenes framed
differently in a screenshot than on screen.

A third front end (Blazor WebAssembly, "kernel in the browser") faces the same
temptation and cannot resolve it the same way: it cannot reference `EngrCAD.Viewer`,
which drags in Avalonia and the desktop Silk.NET bindings. Copying the shaders and the
camera math into a browser client would recreate the exact drift the file exists to
prevent. Hence: extract, don't copy.

## What is here

| Type | What a front end uses it for |
| --- | --- |
| `ViewStyle` | The global view-style selector (Points / Wireframe / Shaded / ShadedWithEdges). |
| `SectionAxis`, `SectionAxisExtensions` | The three axis-aligned cuts a toolbar or CLI exposes. |
| `SectionPlane` | One clip plane (general normal + offset), with `On(axis, offset)`, `On(frame)`, `Through(point, normal)`, `Flipped()`. |
| `SectionCombine` | Intersection (quarter cut / octant) vs Union (each plane cuts independently). |
| `SectionClip` | `Hides` — the shaders' clip rule restated on the CPU, so picking and hover cannot disagree with the render about which corner a quarter cut removed. `Siblings` — the clip set for anything drawn ON a cut face (the SDF isolines). |
| `EffectiveMode`, `RenderModes` | `Resolve(style, displayMode)` is the one precedence rule (explicit non-default part mode wins; default-Shaded parts follow the global style); `SortBackToFront` orders the translucent pass. |
| `ViewerShaders` | The GLSL sources and `MaxSectionPlanes`. `Header(es)` emits either `#version 300 es` or `#version 330 core` — **WebGL2 wants the ES3 one**, which is why the ES header was already there. |
| `CameraState` | The orbit pose (yaw, pitch, distance, target) every front end hands to `CameraMath`. Here rather than in `EngrCAD.Viewer` because the browser client cannot reference that assembly, and a second copy of the pose type is the first step to a second copy of the orbit maths. The namespace is unchanged, so existing call sites were untouched by the move. |
| `CameraMath` | Orbit `Eye`, `LookAt`, `Perspective`/`Orthographic`, the scene-scaled `FrustumPlanes`, `FrameDistance`, `MaxOrbitDistance`, `WriteColumnMajor` (the column-major `float[16]` GL expects) — **and the orbit camera's state transitions**: `Clamped`, `Orbit`, `Zoom`, `Pan`, the input bindings `DragOrbit`/`DragPan`/`DragZoom`/`WheelZoom`/`KeyStep`, and `PitchLimit` (which `ViewCubeMath.PitchLimit` now *is*, so a snap to Top cannot be undone by the very next clamp). |
| `RenderGeometry` | `BuildGridAndAxes` (adaptive 1-2-5 ground grid + RGB axes), `NiceStep`, `SegmentVertices` (line segments -> the xyz vertex array the line program draws). |
| `WireframeEdges` | `Extract(mesh)` — every unique mesh edge as a segment pair, for the wireframe display mode. Moved here from `EngrCAD.Viewer` when the browser front end needed it: it has no GL in it, and **the walk order decides the vertex order in the uploaded buffer**, so two copies would not even upload the same bytes. |
| `Highlight` | Selection gold and the hover strength, in one place: `Strength(index, selected, hovered)` is the `uHighlight` uniform, `LineColor(...)` is what a wireframe or point part gets instead (it has no fill for `uHighlight` to act on). A second front end that re-typed "selection is gold" would be a second definition of what selection *looks like*. |
| `PickMesh`, `PickInstance`, `PickResult`, `ScenePick` | Click picking and hover: unproject a pixel to a world ray (`TryRay`), test it against each visible instance's triangle BVH with Möller–Trumbore, keep the nearest hit the section planes do not remove. `PickMesh` is built per distinct part and shared by its instances (the ray goes into the instance's local space, never the mesh into world space), exactly as the GPU buffers are. |
| `HoverThrottle` | The 4-pixel travel threshold the hover raycast re-picks on. Moved out of `ViewCube.cs` for the same reason `CameraMath`'s drag constants live here: how responsive hover feels is a product decision, not a per-front-end one. |
| `ViewCubeMath`, `ViewCubeAnimation`, `ViewCubeFace`, `ViewCubeGeometry` | The view cube's pure halves: region layout + hit test + the pose table (`PoseFor` is what the desktop toolbar's Front/Top/Right/Iso, the cube's clicks and MCP's named views must all agree on), rotate-snap (`NearestStandardDirection`), the 250 ms smoothstep transition, and the fill/edge/label geometry with its palette (both front ends upload exactly these arrays). The GL widget stays in `EngrCAD.Viewer`'s `ViewCube.cs`. |
| `StrokeFont` | The one polyline glyph table (digits, A–Z, dimension symbols) behind the cube's labels and annotation text. Its source stays pure ASCII — symbol glyphs are keyed by `\u` escapes — for the same reason the shaders do. |
| `AnnotationItem`, `AnnotationCamera`, `AnnotationGeometry` | 3D annotation (PMI) rendering's pure half: the classic dimension anatomy (extension lines, arrowheads, leaders, datum boxes, billboarded screen-constant text), plus the overlay colour. `AnnotationCamera` is a record struct so *value equality* is a layer's rebuild key. The GL layer stays in `EngrCAD.Viewer`'s `AnnotationLayer.cs`. |
| `SectionContourGeometry`, `SectionContours` | The section-plane SDF isolines' pure half: plane frames, the cached `Part.TryGetSdf` route, the marching-squares extraction via `SdfContours.OnPlane`, the lift below the plane that keeps the shader discard from eating the lines, and the three family colours (`ZeroColor`/`PositiveColor`/`NegativeColor`). The GL renderer stays in `EngrCAD.Viewer`'s `SectionContours.cs`. |
| `TabMeshLoader` (+ `TabMeshRequest`/`Batch`/`Progress`/`Failure`/`Completion`) | Lazy tab meshing's state machine: growing-prefix publishing, the generation token, name-the-part-that-throws. Avalonia-free and headlessly tested — but **thread-model-bound** (worker task + post-back delegate), so the single-threaded browser client deliberately does NOT use it; see the EngrCAD.Web README. |
| `MeshFlavor` | The progress line naming the kernel route a part takes (`Lowering to B-Rep...`, `Reticulating splines...` — shown only for geometry that genuinely carries NURBS). Pure graph inspection, no lowering. |

### Why picking is here and not in each front end

A picker that re-derives its own ray unprojection will disagree with the camera the frame
was drawn with, and the disagreement is invisible until someone clicks near an edge — the
same failure `CameraMath` exists to prevent, one layer up. `ViewportControl.HitTest` and
the Blazor viewport's pick are both three lines around `ScenePick.Nearest`.

**Section awareness is built in rather than bolted on.** `ScenePick` takes the plane set
and applies `SectionClip.Hides`, so a surface the cut removed cannot be picked through and
a part with `ClippedBySection` false is never skipped. A front end with no section planes
passes none and pays nothing — which is exactly the state the browser client is in today,
one rung before it grows them.

## What is deliberately NOT here

Anything taking a `GL` stays in `EngrCAD.Viewer` (`RenderCore.cs` there):
`ViewerPrograms.LinkProgram`/`CompileShader`, `SectionUniforms` (the single place either
pass writes the section uniforms), and `RenderUploads.UploadMesh`/`UploadLines`/
`UploadOcclusion`/`SetDefaultOcclusion` — plus the GL halves of the widgets whose pure
halves are listed above: `ViewCube`, `AnnotationLayer`, `SectionContourRenderer`. A
browser client supplies its own WebGL2 equivalents; what it must not supply is its own
shaders, its own camera, or its own copy of what a dimension or a cube face looks like.

## The rule that survives the split

**Never fork shader or camera code between front ends.** Evolve the look here and it
lands in the window, in headless renders and in the browser at once. That now includes
how a drag *feels*: `ViewportControl`'s `Orbit`/`Zoom`/`Pan` and the Blazor viewport's
pointer handlers both call the transitions above, so there is exactly one answer to what
dragging 100 pixels does. And the hard-won
corollary, restated where the sources live: **GLSL source strings must stay pure ASCII**
— one em dash in a shader comment made ANGLE's translator reject the whole shader, the
compile exception aborted `OnOpenGlInit` before the other programs were built, and the
entire viewport rendered black. `EngrCAD.Viewer.Tests` locks this by reflecting over
every `string` field of `ViewerShaders`.
