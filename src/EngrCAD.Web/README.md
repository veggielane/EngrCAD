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

## Status

Kernel-in-the-browser and the WebGL2 interop layer are in place, and the kernel half is
live on the docs site. Still to build: the scene-to-frame layer, the orbit camera
component, feature edges, model tree, picking and section planes. The parity ladder is
in `todo.md`.
