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

- **Toolbar**: Fit (zoom to visible parts), Front/Top/Right/Iso standard views, and a
  perspective/**orthographic** toggle (the ortho frustum keeps the target plane's
  apparent size, so toggling doesn't jump).
- **Model tree** (left): the current tab's parts with visibility checkboxes; clicking
  a name selects it (bold + gold in the viewport), and viewport picks highlight the
  tree row — selection stays in sync both ways.
- **Properties** (right): kind (Shape/B-Rep/mesh/SDF), face count, closed, volume,
  surface area, and world size of the selected part.
- **Viewport dressing**: vertical-gradient background, adaptive ground grid on z = 0
  (1-2-5 spacing from the scene size) with RGB world axes, and a **feature-edge
  overlay** (`MeshFeatureEdges`: boundary + sharp-dihedral edges, drawn over
  polygon-offset fills) — the classic shaded-with-edges CAD look.
- **Status bar** (bottom): last input on the left, control hints on the right.

`EngrCad.Show` may be called once per process (Avalonia allows a single application
lifetime). Custom hosts pass an `onViewportReady` callback, capture the
`ViewportControl`, and later call its thread-safe `SetParts` — GL resources are swapped
inside the next render pass; auto-framing is opt-in per call, so cameras survive
updates. Parts should be pre-meshed (`Scene.PreMesh()`) so tessellation stays off the
render thread.

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
