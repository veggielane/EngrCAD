# EngrCAD.Viewer

Cross-platform viewer **library**: Avalonia UI with an OpenGL viewport rendering kernel
geometry. The only project allowed UI/rendering dependencies (Avalonia, Silk.NET).

## Usage

Design code builds an `EngrCAD.Modeling.Scene` (parts grouped into tabs) and hands it
over:

```csharp
var scene = new Scene();
scene.Add(new Part("bracket", myShape, Palette.Steel));   // default "Model" tab
var drill = scene.AddTab("drill jig");
drill.Add(new Part("jig", jigSolid));
EngrCad.Show(scene, "My design");   // blocks until the window closes
```

Multi-tab scenes get a tab strip; each tab remembers its own camera (auto-framed on
first visit). Picking reports part names in the title bar.

## The CAD chrome

Dark-themed layout around one shared GL viewport:

- **Toolbar**: Fit (zoom to visible parts), Front/Top/Right/Iso standard views, a
  perspective/**orthographic** toggle (the ortho frustum keeps the target plane's
  apparent size, so toggling doesn't jump), the **view-style dropdown** (see below),
  a **Section** toggle with an **X/Y/Z axis cycler** button beside it (see below).
- **Global view style** (`ViewportControl.ViewStyle`, the toolbar dropdown): the
  classic CAD display-style selector — **Points / Wireframe / Shaded / Shaded +
  Edges** — applied to the whole viewport. Precedence rule (one place:
  `RenderModes.Resolve` in `RenderCore.cs`, shared verbatim with the offscreen
  pass): the global style decides how parts render *by default*; a part whose
  `Part.DisplayMode` is explicitly **non-default** (Wireframe or Translucent)
  overrides the global style for that part. `DisplayMode.Shaded` IS the default, so
  it cannot override — parts left at the default follow the global style.
  - *Shaded + Edges* (default) — the current lit-fill + feature-edge look.
  - *Shaded* — the same fills with the feature-edge overlay suppressed (explicit
    Translucent parts keep their silhouette edges — the override wins wholly).
  - *Wireframe* — every part as mesh-edge lines in its color ("mesh" view).
  - *Points* — vertex point sprites (round dots via `gl_PointCoord`, section-clipped
    like everything else; a dedicated point program in `ViewerShaders` — desktop GL
    needs `GL_PROGRAM_POINT_SIZE` enabled, GLES has it always on). Dots are the
    mesh's *actual vertices*: dense on tessellated curved surfaces, sparse on large
    flat faces.
- **Section plane**: the classic CAD section view — an **axis-aligned clip plane**
  (X, Y, or Z — `ViewportControl.SectionAxis`, default Z) at an adjustable offset
  (`SectionOffset`) hides everything beyond it so you can see inside (bores,
  cavities, fillets in cross-section). Implemented as a fragment-shader `discard`
  (`dot(worldPos, uSectionAxis) > uSectionOffset`) in the mesh, line, and point
  shaders; face culling stays off, and the interior surfaces the cut exposes are
  backfaces, which the shader detects via `gl_FrontFacing` and renders as a flat
  darker warm tint — the standard "cut material" cue (axis-agnostic by
  construction). When first enabled the plane defaults to the middle of the parts'
  bounds along the active axis; **changing the axis re-centers it** (an offset on
  one axis is meaningless on another); `[` / `]` move it by 2% of the scene extent
  along the active axis per press (current axis+offset shown in the status bar).
  Feature edges and wireframes are clipped consistently with the fills (they belong
  to the model); the ground grid and world axes are scene furniture and stay
  unclipped. Custom hosts drive `SectionEnabled` / `SectionAxis` / `SectionOffset`
  (`SectionHeight` remains as a delegating legacy alias from the Z-only days).
  Picking ignores the section plane in v1 — a click can select a part through the
  cut-away half.
- **Model tree** (left): the current tab's loose parts and **assembly hierarchies** —
  assembly/sub-assembly header rows with their occurrences indented one level per
  depth (always expanded in v1; the tree walks the tab exactly like
  `Tab.Instances()`, so row order matches viewport instance indices). Visibility
  checkboxes exist at every level: a part row toggles that instance, an assembly row
  hides its whole subtree (effective visibility = own checkbox AND all ancestors;
  unchecking a parent does not touch the children's own state). Clicking a name
  selects the *occurrence* (bold + gold in the viewport), and viewport picks
  highlight the tree row — selection stays in sync both ways and reports occurrence
  paths ("stack/clamp.2/bolt") in the title/status bar. Each part row also has a
  small **display-mode cycler** (`shade` / `wire` / `glass`) that steps the part
  through Shaded → Wireframe → Translucent (see below); the mode lives on the
  shared `Part`, so every instance of that part changes together.
- **Per-part display modes** (`Part.DisplayMode`, default `Shaded`): design code sets
  it (`part.DisplayMode = DisplayMode.Translucent`) and the tree's per-row cycler
  changes it live; custom hosts drive `ViewportControl.SetDisplayMode(index, mode)`.
  - *Shaded* — lit fill with the feature-edge overlay (the normal CAD look).
  - *Wireframe* — every mesh edge drawn as a line, no fill, in the part's color
    (selection turns it gold). Reuses the line program over the half-edge mesh's
    edges (`WireframeEdges`).
  - *Translucent* — alpha-blended fill (α ≈ 0.4) so you can see through to interior
    geometry, with the feature edges drawn opaque on top for a readable silhouette.
    Draw ordering (v1, honest limitations): opaque and shaded parts draw first, then
    translucent parts sorted **back-to-front by part center** with depth-writes off.
    This is correct for separated parts and for a translucent shell over opaque
    contents, but it is a *per-part* sort, not per-triangle — two interpenetrating or
    mutually-overlapping translucent parts, or a single non-convex translucent part
    seen through itself, can show blend-order artifacts. Section mode remains the tool
    for exact interior inspection.
- **Screenshot** (toolbar `Capture`, or `ViewportControl.SaveScreenshot(path?)`): saves
  the current framebuffer as a PNG and reports the path in the status bar. The pixels
  are read with `glReadPixels` *inside* the render pass (the only place GL calls are
  legal), then row-flipped (GL is bottom-up), forced opaque (framebuffer alpha is
  compositing residue), and encoded by a dependency-free `PngWriter` (8-bit RGBA,
  filter None, `System.IO.Compression` deflate) off the render thread. With no path
  the file lands in `Pictures/EngrCAD/engrcad-<timestamp>.png` (falling back to the
  working directory).
- **Properties** (right): occurrence path (plus the part name when they differ),
  kind (Shape/B-Rep/mesh/SDF), face count, closed, volume, surface area, world size,
  and world position of the selected instance.
- **Viewport dressing**: vertical-gradient background, adaptive ground grid on z = 0
  (1-2-5 spacing from the scene size) with RGB world axes, and a **feature-edge
  overlay** (`MeshFeatureEdges`: boundary + sharp-dihedral edges, drawn over
  polygon-offset fills) — the classic shaded-with-edges CAD look.
- **Status bar** (bottom): last input on the left, control hints on the right.

`EngrCad.Show` may be called once per process (Avalonia allows a single application
lifetime). Custom hosts pass an `onViewportReady` callback, capture the
`ViewportControl`, and later call its thread-safe `SetInstances` (posed
`PartInstance` list — `Tab.Instances()`; `SetParts` remains as the loose-part
convenience) — GL resources are swapped inside the next render pass; auto-framing is
opt-in per call, so cameras survive updates. Parts should be pre-meshed
(`Scene.PreMesh()`) so tessellation stays off the render thread.

**Assembly instancing**: both render paths (window and offscreen) upload each
distinct `Part` once — one vertex/edge buffer set and one pick BVH per part,
however many occurrences place it — and draw every instance with its own composed
world matrix (`Frame3d` chain × `Part.Transform`). CPU prep (RenderMesh, feature
edges, BVH) is deduped by part reference. A future optimization is true GPU
instancing (one draw call per part with a matrix buffer); today it is one draw call
per instance over shared buffers, which is already flat in memory.

## The live-modeling loop

Hand `EngrCad.ShowLive` a scene *factory* and run the model under `dotnet watch`:

```csharp
return EngrCad.Run(args, BuildScene, "my bracket");   // ShowLive by default

static Scene BuildScene() { ... }                     // edit + save = live update
```

```
dotnet watch --project samples/EngrCAD.LiveDemo
```

`dotnet watch` hot-reloads method-body edits into the running process; a
`MetadataUpdateHandler` in this library re-invokes the factory and swaps the new scene
in — camera untouched, sub-second. If the factory throws, the last good scene stays and
the error shows in the overlay. Rude edits (signature changes) restart the process;
the camera pose is persisted per title (30-minute freshness window) so the view
survives those too. `EngrCad.Run` also gives every model program standard switches:
`--view` (static show) and `--export part.step|part.stl|part.obj` (headless: STEP for
B-Rep-representable parts, binary STL or OBJ merged with transforms applied —
CI/slicer-friendly, no window).

## Headless offscreen rendering (screenshots without a window)

For tests and AI agents that need to *see* a scene without opening a window,
`EngrCad.RenderToImage` renders straight to a PNG:

```csharp
EngrCad.RenderToImage(scene, "out.png", width: 1280, height: 800);   // auto-framed iso
EngrCad.RenderToImage(scene, "front.png", camera: someCameraState);  // explicit pose
EngrCad.RenderToImage(scene, "wire.png", style: ViewStyle.Wireframe);       // global view style
EngrCad.RenderToImage(scene, "cut.png",                                     // real section plane
    sectionAxis: SectionAxis.X, sectionOffset: 0.0);
```

Headless renders honor everything the window draws — **per-part display modes**
(wireframe, translucent with the same shared back-to-front ordering and opaque
silhouette edges), the **global `ViewStyle`** with the same precedence rule, and
**axis-aligned section planes** (`sectionAxis` + `sectionOffset`; enabled when the
offset is non-null) — so a headless PNG matches what the viewer shows, and docs
cutaways can use real section planes instead of boolean-cut workarounds.

`EngrCad.Run` exposes it as a switch too: `--render out.png` renders and exits, no
window (alongside `--view` and `--export`). Check `EngrCad.CanRenderToImage` first to
skip gracefully on machines with no GPU/ANGLE.

- **No window, no Avalonia lifetime.** `OffscreenRenderer` renders into an offscreen
  **EGL pbuffer** created directly over the ANGLE runtime Avalonia already ships on
  Windows (`av_libglesv2.dll` from Avalonia.Angle.Windows.Natives exports both the GLES
  and the EGL entry points — the latter with an `EGL_` prefix). `EglContext` P/Invokes
  `eglGetPlatformDisplayEXT`/`ChooseConfig`/`CreatePbufferSurface`/`CreateContext`/
  `MakeCurrent`, preferring the D3D11 hardware backend, then D3D11-on-WARP (software —
  works on CI and locked sessions), then the default display. The same Silk.NET `GL`
  surface then draws the scene and `glReadPixels` reads it back.
- **The look matches the viewport by construction**: both passes draw with the shared
  render core in `RenderCore.cs` — `ViewerShaders` (ONE shader set; the only feature
  the offscreen pass neutralizes is the selection highlight, uHighlight 0 — there is
  no interactive selection offscreen), `RenderModes` (the global-style x per-part-mode
  precedence and the translucent back-to-front sort), `CameraMath`
  (LookAt/projection/column-major writer, the scene-scaled near/far frustum, and the
  auto-framing distance), and `RenderGeometry` (grid/axes builder, line/mesh upload).
  Evolve the look there and it lands in the window and in headless renders at once; do
  not re-fork per-pass copies. Shader sources must stay pure ASCII (ANGLE rejects the
  whole shader on any non-ASCII byte — this once black-screened the viewport).
- **`PngWriter`** is a tiny dependency-free 8-bit RGBA PNG encoder (one zlib IDAT via
  `System.IO.Compression`); no image library is pulled in.

## How it works

- **`ViewportControl`** extends Avalonia's `OpenGlControlBase` and adapts its
  proc-loader into a Silk.NET `GL` API object, giving the full modern GL surface over
  whatever context Avalonia provides — desktop OpenGL 3.3+ or, on Windows, OpenGL ES 3
  via ANGLE (shaders are compiled with a version header chosen at runtime).
- Meshes from any engine are turned into `RenderMesh` (flat-shaded) buffers and drawn
  with a simple directional-light shader.
- **Camera** (laptop-friendly): drag orbits, Shift+drag pans, Ctrl+drag or scroll zooms;
  right/middle-drag also pans. Keyboard works everywhere: arrows orbit, +/−
  (or PageUp/Down) zoom, WASD pans. Z is up.
- **Input plumbing**: all pointer/keyboard handlers are registered on the *window* with
  `handledEventsToo` — control-level handlers proved fragile (gesture recognizers and
  hit-testing over the GL surface starved the viewport of events, breaking trackpads).
  The overlay's second line reports the last input received, which makes input problems
  diagnosable at a glance.
- **Picking**: click selects the nearest part under the cursor (unprojected ray +
  per-object triangle BVH + Möller–Trumbore); the selection is highlighted gold and its
  *part name* shown in the title bar; clicking it again deselects. `ShowStatus(string)`
  lets a host surface messages (script errors) in the overlay.

## Demo

The showcase scene lives in `samples/EngrCAD.Demo` (a console app using the Scene API —
the exact consumer experience):

```
dotnet run --project samples/EngrCAD.Demo
```
