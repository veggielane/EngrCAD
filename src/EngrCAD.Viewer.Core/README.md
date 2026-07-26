# EngrCAD.Viewer.Core

The **UI-free render model**: everything both EngrCAD render paths agree on that does
not touch a GL binding. No Avalonia, no Silk.NET, no `System.Drawing` — this assembly
must load in a WebAssembly client, so its only references are `EngrCAD.Core` (math
structs), `EngrCAD.Mesh` (the half-edge walk `WireframeEdges` does) and
`EngrCAD.Modeling` (`DisplayMode`).

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

## What is deliberately NOT here

Anything taking a `GL` stays in `EngrCAD.Viewer` (`RenderCore.cs` there):
`ViewerPrograms.LinkProgram`/`CompileShader`, `SectionUniforms` (the single place either
pass writes the section uniforms), and `RenderUploads.UploadMesh`/`UploadLines`/
`UploadOcclusion`/`SetDefaultOcclusion`. A browser client supplies its own WebGL2
equivalents; what it must not supply is its own shaders or its own camera.

Also not here: `StrokeFont`, `ViewCubeMath`, `AnnotationGeometry` and `SdfContours`.
They are pure too, and a browser front end will want them — but they were not part of
`RenderCore.cs` and moving them is a separate, testable step, not a drive-by.

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
