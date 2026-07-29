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
| `Animation`, `AnimationSample`, `AnimationEasing`, `AnimationTrack` | The animation timeline: duration + easing + at most one pose track, one camera track and one deformation track, each with a clamp-semantics window on the shared timeline. `At(t)` is a **pure function of t ∈ [0,1]** — scrubbing, reversing, playback and every export format evaluate the same function. The load-bearing rule (inherited from the exploded view): **an animation must not touch geometry** — tracks return poses over a fixed instance list (count and order independent of t), a camera, or a scalar; never a re-meshed part, so `SetInstancePoses` animates with matrices alone and picking keeps working. Lives here by dependency direction: pose tracks speak `Scene`/`Mechanism` (Modeling) and camera tracks speak `CameraState`, and Modeling cannot reference the camera types without a cycle — the cost is that a `Scene` cannot carry its animation as a typed property (hosts take it beside the scene). |
| `DeformationTrack`, `DeformationTracks` | Scales a displayed result's deformation: a factor multiplying every part's own `FieldDisplay.DeformScale`, reaching all three front ends as ONE `uDeformScale` uniform. **A deformed shape looked like the exception to the no-geometry rule and is not** — the displacement travels once as a vertex attribute (see `FieldRendering`), so animating it changes a number, and the rule stands with nothing weakened. `LoadRamp` (0 → peak → 0, both ends exactly 0 so a clip loops) is the first demo and is **honest for a linear solve**: a linear result scales exactly, so the intermediate frames are the actual answers for the intermediate loads rather than a tween. `Oscillate(amplitude, cycles)` is the mode-shape animation — vibrating in a mode IS the shape times `cos(ωt)` — with three caveats it carries in its own doc comment: a mode shape has no physical amplitude, its sign is a convention, and **it does not play at its own frequency**. That last one is the trap: `cycles = frequency × duration` is dimensionally right and useless, since a steel blade 80 mm long and 6 mm thick rings near 780 Hz and a two-second clip would need ~1570 cycles — hundreds per frame, aliasing into blur, and no frame rate fixes a mode that is faster than video. Use a small fixed `cycles` and state the slowdown factor. `Ramp`, `Constant` and `From(law)` complete it; sequencing lives inside one law, since two factors on one scale have no defined composition. |
| `MechanismTrack` | Plays a swept `MotionStudy`: recorded frames returned **verbatim at their sample points** (bit-exact, locked by test), chordal rigid interpolation between them (rotation via quaternion slerp of the rigid delta `b·a⁻¹` — rigid whatever the part transform carries, since both matrices share it — origin along the straight chord). Never a re-solve: solving at arbitrary t from an arbitrary seed is the branch-flipping trap the sweep's continuation exists to avoid. The `(study, scene)` overload grafts poses onto the scene's instance list by occurrence path so bystander parts stay put and the output matches the viewport index-for-index. |
| `ExplodeTrack` | Animates the exploded view through `Scene.Instances(Func<Occurrence, double>)` — the same flatten walk as the scalar factor, so factor exactly 0 leaves frames bit-identical. `Stagger(occurrence, start, end)` gives per-occurrence timing windows: fasteners back out first, then the cover — the sequenced explode. Construction derives missing offsets once (`Scene.AutoExplode`), so construct off the render thread. |
| `AnimationPlayback` | The transport state machine: play/pause/loop/seek plus a clock-driven `Advance(dt)` that wraps the overshoot on loop (playback speed independent of the timer's tick quantum). UI-free so the desktop toolbar, a future web transport and the tests drive the same machine — the front end owns only a timer and widgets, and renders `Animation.At(T)`, the same pure function exports evaluate. |
| `TurntableTrack`, `KeyframedCameraTrack`, `CameraKeyframe`, `FlyThroughTrack` | Camera tracks: turntable (orbit about Z at fixed pitch; whole turns loop seamlessly under linear easing; `Around(scene)` bases on `CameraMath.DefaultCamera`), keyframed poses with the view cube's transition feel (per-segment `ViewCubeMath.Ease` + **shortest-yaw-path** via `ShortestYawTarget` — the cube's primitive reused, not re-derived), and a fly-through along any `Curve3d` (eye on the curve, looking along the tangent or at a fixed point; the orbit pose is Z-up so a full RMF frame's roll is documented as dropped, and vertical tangents clamp through `ViewCubeMath.PoseFor`). |
| `TabMeshLoader` (+ `TabMeshRequest`/`Batch`/`Progress`/`Failure`/`Completion`) | Lazy tab meshing's state machine: growing-prefix publishing, the generation token, name-the-part-that-throws. Avalonia-free and headlessly tested — but **thread-model-bound** (worker task + post-back delegate), so the single-threaded browser client deliberately does NOT use it; see the EngrCAD.Web README. |
| `MeshFlavor` | The progress line naming the kernel route a part takes (`Lowering to B-Rep...`, `Reticulating splines...` — shown only for geometry that genuinely carries NURBS). Pure graph inspection, no lowering. |
| `ColorMaps` | The tables behind `FieldColorMap` (Modeling): `Viridis` — a 17-stop piecewise-linear sampling of matplotlib's viridis, monotone in lightness so it reads in greyscale and under colour-vision deficiency — and `Diverging` (Moreland's cool-to-warm, 9 stops, neutral grey midpoint). Tables and not formulas on purpose: a perceptual map is *measured*, and a polynomial fit would be a second approximation with nothing to check it against. `Sample(map, range, value)` composes `FieldRange.Normalize` with the table, so fills, legend and any probe readout cannot disagree about a value's colour. Here for the same reason `RenderModes.Resolve` is: the enum is a document-model choice, this is the one implementation of what it looks like. |
| `PartUpload`, `PartUploads`, `PartUploadRequest` | The CPU half of "draw this part", shared by all three front ends: `Build(part, request)` meshes it, builds the flat `RenderMesh`, resolves the field colour/displacement buffers, collects the feature-edge and wireframe segments, runs the caller's occlusion source and builds the pick BVH. **Two things it deliberately does not do**, and both are why the larger `ViewerModel` stayed declined. It does not decide WHICH pieces to build — the one-shot offscreen pass skips what its resolved mode cannot use, while the window and the browser build everything so a style dropdown never re-uploads, so the caller states its policy in a `PartUploadRequest` (`All` is a spelling, not a rule). And it does not own the cache: all three key uploads on `Part` reference, but the browser releases on tab switch, the window on GL deinit and the offscreen pass with its context. Occlusion arrives as a **delegate** rather than a flag because the window asks a never-bake cache read (an upload must never stall the render thread) while the offscreen pass bakes inline to stay deterministic — two different questions. What it *does* own is every rule about the CONTENT, including the one that had been written out three times: **a part carrying a displacement draws no feature-edge overlay at any factor** (those edges describe geometry that has moved, and the draw list must not depend on an animation's `t`, or a clip could not reuse one upload). |
| `FieldRendering`, `FieldMeshData` | Turns a `Part`'s results into the buffers a pass uploads: `SourceColors(...)` (one colour per SOURCE mesh vertex — the map sampled once per value) feeding `Colors(...)` (RGB per render vertex, spread across the flat mesh's duplicates through `RenderMesh.SourceVertices`; the glTF exporter takes `SourceColors` directly and does its own spreading, so the two cannot compute different colours for one field), and `DeformationAttributes(...)` — the displacement per UNIT scale plus the three coefficients of the displaced facet normal, four interleaved vec3s at slots 4–7. `TryBuild` resolves, validates lengths and reports by name; a part with no display returns false with a *null* error, the "nothing to show" vs "it went wrong" distinction `Part.TryGetSdf` draws. **Both colour and displacement are vertex attributes under the same constant-when-absent rule** the occlusion attribute established, so a part with no results renders **byte-identically** whatever `uDeformScale` says — and that is what makes animating a deformed result ONE float uniform per frame instead of a re-upload per frame. The exact-normal identity is the enabling fact: a triangle whose vertices move linearly in s has a facet normal that is exactly QUADRATIC in s, so three coefficients reproduce at every scale what the CPU path recomputed. `Deform(...)` survives for one job only — `PickShape(...)`, the geometry a pick BVH indexes, built at the part's own scale because a spatial index cannot be a uniform (so picking deliberately does not follow an animation's factor). `DeformUniform` forms the part-scale × factor product in double and narrows once, which is what makes an animated frame byte-identical to a static render of the same configuration; `AtFactor` scales a resolved display for the legend, whose title states the number. |
| `GltfScene`, `GltfPlan` | Turns a `Scene`/`Tab`/`Assembly` into the node forest `GltfWriter` (EngrCAD.Mesh) writes: a node per tab, per sub-assembly and per occurrence, with **one mesh per distinct `Part`** however many times it is placed — the structural decision `StepWriter.WriteAssembly` makes, and what separates glTF from the baking exporters. Occurrence frames (including `ExplodeOffset`, composed exactly as `Assembly.Flatten` does, so an exported exploded view and a rendered one agree) become node matrices rather than baked vertices; the flat `IReadOnlyList<PartInstance>` overload keeps the instancing for callers holding a filtered list rather than a document. **Here and not in Modeling** because a glTF file is "what you see" written down, and everything that decides that already lives here — `ColorMaps`, `FieldRendering.SourceColors`, `DisplayMode`'s translucency; the alternative was a second copy of them or a Modeling→Viewer reference. Result colours travel (the *same* array the viewport uploads, so a plot in a browser and a plot on screen cannot disagree) but the **deformation exaggeration deliberately does not**: it is a viewing parameter, glTF has nowhere to record one, and a file carrying 50×-displaced geometry would be indistinguishable from a model that really is that shape. A part that will not mesh is named in `Skipped`, never swallowed. |
| `FieldLegend`, `FieldLegendGeometry` | The colour bar: flat-coloured bands (the line program is flat-colour, and a bar of discrete steps is arguably more readable — a value lands in a band you can point at), an outline with ticks, stroke-font tick numbers and a title stating the units and any deformation exaggeration. Laid out in framebuffer pixels with a pixel-coordinate `Projection`, scaled by the same pixel scale point sprites and annotation text use. Left edge, because the cube owns the top-right and the meshing panel the bottom centre. |

| `ParamEditors`, `ParamEditorKind` | Which affordance a properties panel gives a value, decided from metadata the feature registry already carried: `KindFor` (bool → checkbox, enum → dropdown, finite-range numeric → slider, else text) plus `HasRange`/`IsWhole`/`Position`, and `MaterialChoices`/`MaterialLabel` — the material dropdown's rows, "(none)" first, then `Materials.All`, then the part's own material when the catalogue does not carry it (a design-built one, or a `FastenerMaterials` grade a catalogue component brought with it; a control that cannot show the current value reads as "not set", and one idle click would discard it). Pure, so the rule is asserted as a value and a second front end cannot grow its own opinion about what an editor looks like. The constraint one level down is not expressed here: **whatever editor is chosen writes through the same seam** — `FeatureHistory.SaveParameters`' JSON, `DocumentEdits.SetMaterial` — so a typed editor is a better way to SAY a value, never a second way to apply one. |

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
`UploadOcclusion`/`SetDefaultOcclusion`/`UploadFieldColors`/`SetDefaultFieldColor` — plus
the GL halves of the widgets whose pure halves are listed above: `ViewCube`,
`AnnotationLayer`, `SectionContourRenderer`, `FieldLegendLayer`. A
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
