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

## Geometry crosses the boundary as bytes

Blazor marshals `byte[]` as a binary array; `float[]` goes through JSON. For a mesh of a
few hundred thousand floats that is the difference between a copy and a stall, so
`WebGlContext` packs to bytes on the .NET side and JavaScript reinterprets them as
`Float32Array`/`Uint32Array`. `Vector3d` is doubles and GL wants float32, so the packing
step is also the single narrowing point.

## Measured: what running the kernel in a browser actually costs

A flange with a 6-hole bolt circle and a filleted rim — one boolean, six drilled holes,
a rim feature, so the boolean, face-splitting and trimmed-tessellation paths all run.
Best of five in headless Edge on win-arm64, against the same model on the desktop
runtime:

| | lower to B-Rep | tessellate | total | payload (brotli) |
| --- | --- | --- | --- | --- |
| Desktop (native) | 35.9 ms | 45.4 ms | **81.3 ms** | — |
| WASM, no AOT | 790.1 ms | 829.6 ms | **1 619.8 ms** (19.9×) | **2.4 MB** |
| WASM, AOT | 190.5 ms | 211.0 ms | **401.6 ms** (4.9×) | **4.6 MB** |

Three things worth taking from that:

- **Correctness is not in question.** The browser produced 1 560 triangles, a closed
  mesh, and volume 41 573.0 — the same numbers as the desktop run, to the precision
  displayed. The kernel does not need a WASM-specific code path.
- **AOT is worth 4×, and costs 1.9× the payload.** `wasm-tools` plus
  `-p:RunAOTCompilation=true`. Which side of that trade to take is a product decision:
  interactive editing wants AOT, a docs page that draws one static model probably does
  not.
- **The kernel is not the payload problem.** All eight EngrCAD assemblies together are
  1.17 MB uncompressed; the 2.4 MB download is dominated by the .NET runtime itself
  (3.0 MB `dotnet.native.wasm` + 1.6 MB `System.Private.CoreLib`). Trimming our own code
  would win almost nothing.

Numbers come from the demo's `?report` self-check — see its comment for why in-page
timing has to be beaconed out rather than read from a DOM dump.

## Status

Kernel-in-the-browser and the WebGL2 interop layer are in place. Still to build: the
scene-to-frame layer, the orbit camera component, feature edges, model tree, picking and
section planes. The parity ladder is in `todo.md`.
