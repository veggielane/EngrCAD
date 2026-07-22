# EngrCAD.Viewer

Cross-platform desktop viewer: Avalonia UI with an OpenGL viewport rendering kernel
geometry. The only project allowed UI/rendering dependencies (Avalonia, Silk.NET).

## How it works

- **`ViewportControl`** extends Avalonia's `OpenGlControlBase` and adapts its
  proc-loader into a Silk.NET `GL` API object, giving the full modern GL surface over
  whatever context Avalonia provides — desktop OpenGL 3.3+ or, on Windows, OpenGL ES 3
  via ANGLE (shaders are compiled with a version header chosen at runtime).
- Meshes from any engine are turned into `RenderMesh` (flat-shaded) buffers and drawn
  with a simple directional-light shader.
- **Camera** (laptop-friendly): drag orbits, Shift+drag pans, Ctrl+drag or scroll zooms;
  right/middle-drag also pans. Z is up.
- **Picking**: click selects the nearest object under the cursor (unprojected ray +
  per-object triangle BVH + Möller–Trumbore); the selection is highlighted gold and named
  in the title bar; clicking it again deselects.
- The demo scene exercises the whole kernel: mesh primitives, Loop subdivision, a mesh
  boolean, SDF-derived meshes (smooth blend, torus, gyroid lattice via Surface Nets), and
  B-Rep modeling results (extruded bracket, revolved pulley, swept tube).

## Running

```
dotnet run --project src/EngrCAD.Viewer
```
