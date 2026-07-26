# In the browser (WebAssembly)

Every other page on this site shows a model rendered ahead of time and committed as a
PNG. This one doesn't. The panel below is the **actual geometry kernel** — B-Rep,
implicit and mesh — compiled to WebAssembly and running in your tab. Move a slider and
the model is rebuilt from scratch: a boolean, six drilled holes and a rim fillet,
lowered to an exact B-Rep and tessellated, right here.

<!-- docfx warns "InvalidFileLink" on the two ../live/ links below, and that is EXPECTED:
     the target is the Blazor app, which .github/workflows/docs.yml publishes into
     _site/live/ AFTER docfx has run, so no such file exists at docfx time. Both links are
     emitted verbatim, which is all that matters. Do not "fix" this by writing an absolute
     /EngrCAD/live/ path -- that bakes the repository name into the site and breaks the
     moment it is renamed or served from a user page. -->

<iframe src="../live/?embed"
        title="EngrCAD's geometry kernel running in WebAssembly"
        loading="lazy"
        style="width:100%; height:26rem; border:1px solid #3a3d45; border-radius:6px; background:#1d1f24;">
</iframe>

<p style="margin-top:.5rem"><a href="../live/">Open the demo full-page →</a></p>

If that panel is empty, your browser blocked WebAssembly or the demo hasn't been
deployed yet — see *Running it yourself* below.

## The model

This is the same `Shape` code you would write for the desktop viewer. Nothing about it
is web-specific, and that is the point:

```csharp run:wasm-flange
int holes = 6;
double fillet = 2;

var points = new List<Vector2d>(holes);
for (int i = 0; i < holes; i++)
{
    double angle = 2 * Math.PI * i / holes;
    points.Add(new Vector2d(28 * Math.Cos(angle), 28 * Math.Sin(angle)));
}

var body = Shape.Cylinder(40, 10) - Shape.Cylinder(14, 30);   // bored disc
var top = SketchPlane.At((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);
var flange = body.Drill(StandardHoles.Clearance(6), points, depth: 14, top)
                 .Fillet(fillet, s => s.PlanarFacesWithNormal(Vector3d.UnitZ));

// The figures this page quotes for the browser, asserted here against the desktop
// kernel: if the two ever stop agreeing, the docs build is what says so.
var mesh = flange.ToMesh();
if (mesh.FaceCount != 1560 || !mesh.IsClosed || Math.Abs(mesh.Volume() - 41573.0) > 0.1)
    throw new Exception($"flange changed: {mesh.FaceCount} tris, closed={mesh.IsClosed}, "
                        + $"volume={mesh.Volume():F1}");
```

A flange rather than a box on purpose: between them the boolean, face-splitting and
trimmed-tessellation paths dominate kernel time, so the timings the panel reports mean
something.

## What it costs

Headless Edge on win-arm64, best of three page loads each running best-of-five builds,
from **clean** publishes, with the desktop control interleaved so all three rows share
one machine state (this laptop swings ~2× between runs of the *same* binary, so a ratio
taken across sittings would be noise with units):

| | lower to B-Rep | tessellate | total | payload (brotli) |
| --- | --- | --- | --- | --- |
| Desktop (native) | 36.4 ms | 52.3 ms | **88.7 ms** | — |
| WASM, no AOT | 818.6 ms | 858.6 ms | **1677.3 ms** (18.9×) | **1.9 MB** |
| WASM, AOT | 178.8 ms | 206.4 ms | **385.2 ms** (4.3×) | **4.6 MB** |

Three things worth taking from that:

- **Correctness is not in question.** The browser produces 1 560 triangles, a closed
  mesh and volume 41 573.0 mm³ — the same numbers as the desktop run, to the precision
  displayed. There is no WASM-specific code path in the kernel, and no trouble from the
  `ArrayPool` / `stackalloc` / `Vector<double>` machinery the performance mandates rely
  on. WebAssembly is a **speed tier, not a port**.
- **AOT buys 4.4× for 2.4× the download** (`wasm-tools` plus
  `-p:RunAOTCompilation=true`). Which side of that trade to take is a deployment
  decision. This page ships the *non*-AOT build for two reasons: AOT compilation adds
  several minutes to every documentation deploy, and a slider that rebuilds the whole
  model on release is a *transitional* demo — once the WebGL viewer lands you will orbit
  a cached mesh rather than re-lower a B-Rep, and the rebuild cost stops being what the
  page is about. An interactive editor would choose the other way.
- **The kernel is a fifth of the download, and the runtime is the rest.** All nine
  EngrCAD assemblies come to 1.14 MB uncompressed and 0.41 MB brotli, against a 1.9 MB
  total; the single largest items are `System.Private.CoreLib` (1.53 MB) and
  `dotnet.native.wasm` (1.43 MB) uncompressed. Trimming our own code could win at most
  a few hundred kilobytes.

## No GLSL in JavaScript

The rendering half is `src/EngrCAD.Web`, a Razor component library over WebGL2. Its one
architectural rule is that **`engrcad-gl.js` contains no policy**: it owns the GL
context, uploads the buffers it is handed and issues the draws it is told to. Shader
source comes from `EngrCAD.Viewer.Core`'s `ViewerShaders` — the *same strings* the
desktop window and the offscreen renderer compile — and camera framing, section
clipping and draw order all reach JavaScript as a plain frame description.

That is not tidiness. The desktop viewer already had this exact problem once: the
window and the offscreen renderer duplicated their shader and camera code and drifted
apart silently, which is why the shared render core exists. A WebGL client with its own
copy would be that mistake a third time, in a language where nothing would catch it.
The test of the rule is simple — if a question about what the scene *looks like* can be
answered by reading the JavaScript, the rule has been broken.

Geometry crosses the boundary as `byte[]`, because Blazor marshals that as a binary
array while `float[]` would go through JSON. That packing step is also the single
place doubles narrow to the float32 the GPU wants.

## Running it yourself

```
dotnet run --project samples/EngrCAD.WebDemo
```

To reproduce what this page embeds — a publish served from a subdirectory:

```
dotnet publish samples/EngrCAD.WebDemo -c Release -o out
```

**Clear `obj`, `bin` and the output directory first.** Republishing a Blazor WASM app
over a previous publish can ship a runtime that disagrees with the assemblies, and it
fails at *run* time with a Mono interpreter abort rather than at build time — the publish
reports success throughout. A fresh CI checkout is immune; local iteration is not.

Then serve `out/wwwroot` from anywhere. The published app is **path-portable**: its
`index.html` uses a relative `<base href="./" />` and every asset reference the build
emits is relative, so it runs from a site root or from `/EngrCAD/live/` with no
rebuild and no repository name compiled into it.

## Status

Kernel-in-the-browser and the WebGL2 interop layer are in place; the demo above is the
kernel, not yet the viewer. Still to build: the scene-to-frame layer, the orbit camera,
feature edges, model tree, picking and section planes — the parity ladder is in
`todo.md`. The [Viewer](viewer.md) page describes what the desktop client already does,
which is the target.
