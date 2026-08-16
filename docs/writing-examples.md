---
title: "Writing documentation examples"
---

Every C# example in these docs is **executed by the documentation build**
(`tools/EngrCAD.DocsGen`), and rendered examples produce their screenshots
automatically. A snippet that stops compiling or throws fails the build, so the
examples cannot drift from the code.

## The snippet convention

The fence *info string* tags a snippet for the generator:

````
```csharp render:my-example-id
var scene = new Scene();
scene.Add(new Part("demo", Shape.Sphere(10)));
```
````

| Fence | Meaning |
| --- | --- |
| ` ```csharp render:<id> ` | Executed. Must end with a variable **`scene`** of type `Scene` in scope; the generator renders it to `examples/images/<id>.png`, and the same page must reference that image (`![alt](images/<id>.png)`). |
| ` ```csharp animate:<id> ` | Executed. Must define `scene`, and may define an **`animation`** (`Animation`); without one it gets a default 4-second turntable. Rendered to an **APNG** at `examples/images/<id>.png` — an APNG *is* a PNG, so the reference rule is unchanged and browsers just play it. Mind the build time and committed size: every frame is an offscreen render, so keep `frames:` modest. |
| ` ```csharp svg:<id> ` | Executed. Must define a variable **`svg`** of type `string` (e.g. `var svg = sheet.ToSvg();`), written verbatim to `examples/images/<id>.svg` and referenced the same way (`![alt](images/<id>.svg)`). For a **drawing sheet**, which is line work on paper rather than a render — it has no camera, no lighting and no pixels, and rasterizing it would throw away the one property that makes it useful. Needs no GL, so it works on any machine. |
| ` ```csharp run:<id> ` | Executed for correctness only — no screenshot. Use for exports, queries, and other non-visual examples. Throw on unexpected results so regressions fail the build. |
| ` ```csharp ` | Display-only. Not executed — use sparingly, for fragments that cannot stand alone (project files, switch tables, viewer-interactive calls like `EngrCad.Show`). |

Ids are lowercase `[a-z0-9-]`, globally unique across the docs.

`render:` fences accept optional **render options** after the id, mirroring
`EngrCad.Run`'s `--render-style`/`--section` switches — use them for real section
planes instead of boolean-cut fakes:

````
```csharp render:my-cutaway section:z,6 style:shaded
```
````

| Option | Meaning |
| --- | --- |
| `style:<points\|wireframe\|shaded\|shaded-edges>` | Global view style for the screenshot (default `shaded-edges`). |
| `section:<x\|y\|z>,<offset>` | Renders with a real axis-aligned section plane at the offset (SDF-routed parts get their isoline overlay on the cut). Repeat with `;` for a quarter or octant cut: `section:x,0;y,0`. |

`animate:` fences take `style:<name>` and `frames:<2..120>` (default 24). The
snippet's `animation` variable (and `camera`, when there is no camera track) rides
the same declared-variable convention as `render:` fences. A `render:` fence may also
declare `var renderSize = (width, height);` — a `(int, int)` tuple — to override the
default 1600×1120 (a portrait figure, for instance); the 2×-the-display-size rule stays
the author's to honour.

Unknown options fail the build.

### Render inputs the fence cannot spell

An oblique plane, a plane *list* with an explicit combine rule, or a specific camera
pose would each need its own mini-language in the info string. Instead a `render:`
snippet may **declare them as variables**, exactly the way it already declares
`Scene scene`:

| Variable | Type | Effect |
| --- | --- | --- |
| `scene` | `Scene` | **Required.** What gets rendered. |
| `sectionPlanes` | any `IEnumerable<SectionPlane>` | Section planes, oblique allowed (`SectionPlane.Through(point, normal)`, `SectionPlane.On(frame)`). Wins over the fence's `section:`. |
| `sectionCombine` | `SectionCombine` | `Intersection` (default — the quarter/octant cutaway) or `Union`. |
| `camera` | `CameraState` | An explicit pose instead of the auto-framed iso view. |
| `explode` | `double` | Exploded-view factor (0 assembled → 1 fully exploded). Derives occurrence offsets via `Assembly.AutoExplode` if the design has not set them. |
| `shading` | `ShadingStyle` | How fills are lit: `Lit` (default), or the analytic matcaps `Clay`/`Metal`. |
| `annotationDepth` | `AnnotationDepth` | How 3D annotations treat material in front of them: `AlwaysOnTop` (default) or `Occluded` (lines behind the part are dimmed; the values stay legible). |
| `preview` | `ConstructionPreviewRequest` | One construction-tree row (`new(part, part.ConstructionTree()!.Find(path)!)`) drawn over the render as the model tree's rollback view — construction-cyan edges, always on top. A row that cannot be lowered fails the build. |

`EngrCAD.Viewer` is imported for exactly these types. A variable of the right name but
the **wrong type is an error**, never a silent miss — an example that quietly ignored
its own section plane would be a trap. See
[the viewer page](examples/viewer.md#several-planes-and-oblique-ones) for a worked
oblique quarter cut.

## Running an example in the browser

Most rendered examples carry a **Run it in your browser** button under their screenshot.
It swaps the picture for the geometry kernel itself, compiled to WebAssembly, building
*that example's* model in the reader's tab — see [In the browser](examples/web.md).

Nothing in the markdown asks for this and there is no fence option for it. The
documentation build compiles every snippet already, so it compiles each one a **second
time against exactly the assemblies the WebAssembly viewer carries** and emits the result
as a small standalone assembly (about 6 KB); the site reads
`docs/examples/live-examples.json` to decide which screenshot gets a button.

**The screenshot stays, and stays the default.** It is what the page is for, it is the
build's own regression oracle, and the runtime is megabytes — so the viewer starts on a
click, never on page load, and is then cached for every other example the same reader
opens.

### Why an example might not run

The browser's reference set *is* the rule: whether an example can run there is answered by
the C# compiler, not by a list somebody maintains. An example is refused, by name and with
the refusing tool's own words, when it:

| Reason | Examples affected today |
| --- | --- |
| uses `EngrCAD.Fea` — the simulation layer is not in the browser payload | the seven FEA pages' figures |
| uses the desktop viewer (`EngrCad.RenderToImage`, `ConstructionPreviewRequest`) rather than its UI-free half | `construction-preview` |
| uses the docs-only `Scratch` directory (the browser build supplies no globals) | `import-drilled` |
| reads the build machine's filesystem or environment, which the browser has no copy of | the two `text.md` figures, which load a system font |

`run:` and `svg:` fences are not offered at all — they define no `scene`.

The declared render inputs travel with the example: `camera`, `sectionPlanes`,
`sectionCombine`, `explode` and `shading` are applied to the live viewport, so it is
looking at the same thing from the same place the screenshot was taken.

The button is a progressive enhancement. With no JavaScript, or in a local `npm run dev`
preview where `/live/` has not been merged in, the page is exactly the screenshot it
always was.

## The harness contract

Each tagged snippet runs as an isolated C# *script* (Roslyn scripting) with:

- **Implicit usings**: `System`, `System.IO`, `System.Linq`,
  `System.Collections.Generic`, and the EngrCAD namespaces `EngrCAD.Core`,
  `EngrCAD.Mesh`, `EngrCAD.Implicit`, `EngrCAD.BRep`, `EngrCAD.Interop`,
  `EngrCAD.Query`, `EngrCAD.Modeling`, `EngrCAD.Core.Geometry2`, and `EngrCAD.Viewer`
  (for the render-input types above only — snippets never open windows; the generator
  does the offscreen rendering itself).
- **References**: all EngrCAD kernel assemblies.
- **A global `Scratch`** (`string`): a writable temp directory for snippets that
  produce files (STL/STEP/OBJ exports). Never write anywhere else.
- Scripts may declare classes (the [parametric features page](examples/features.md)
  defines `Feature` subclasses inline).

Render snippets are screenshotted with the viewer's offscreen renderer
(`EngrCad.RenderToImage`, 1000×700, auto-framed isometric view — the same framing the
viewer uses on first show).

## Running the generator

```
dotnet run --project tools/EngrCAD.DocsGen -- docs
```

- Scans `docs/**/*.md` (excluding generated trees — `api/`, `_apisite/`, and the site's
  own `node_modules/`, `dist/`, `.astro/`), executes every tagged snippet in file order,
  writes PNGs to `docs/examples/images/<id>.png`.
- Also emits each runnable example's browser copy into
  `samples/EngrCAD.WebDemo/wwwroot/examples/` (build output, gitignored — the demo's
  publish picks them up as static assets) and rewrites the committed
  `docs/examples/live-examples.json`. `--no-live` skips that pass; `--live <dir>` sends
  the assemblies somewhere else.
- **Exit code is nonzero** if any snippet fails to compile or run, a `render:`
  snippet defines no `scene`, an id is duplicated, or a page never references its
  generated image.
- On machines where offscreen GL is unavailable (`EngrCad.CanRenderToImage == false`,
  e.g. a bare CI runner), rendering is skipped with a warning and the **committed**
  PNGs are used — execution failures still fail the build. Generated PNGs are
  committed to the repository for exactly this reason.

## Adding a page

A page is a markdown file in `docs/examples/` with **YAML frontmatter carrying a
`title`**, which is what Starlight renders as the page's heading — so the body starts at
the first `##`, not at an `#`:

```
---
title: "Sheet metal"
---

A sheet part is a flat blank plus a list of bends...
```

Then add it to the `sidebar` in `docs/site/astro.config.mjs`. The order there is the
site's navigation and is deliberate; it used to live in `docs/toc.yml`. A page left out
of it still builds and is still reachable by URL, so nothing would complain — hence
`check-links.mjs` asserts that every built page appears in the sidebar and fails the
build naming the one that does not.

Links between pages are written as ordinary **relative markdown links** (`[fields](fields.md)`,
`[the viewer](../examples/viewer.md#matcap-shading)`), so the documentation stays
navigable in the repository. A rehype plugin (`docs/site/src/rewrite-doc-links.mjs`)
turns them into the routes the site serves, and **throws when the target file does not
exist**, so a renamed page fails the build at both ends rather than 404ing for a reader.

## Building the site

The site is [Astro Starlight](https://starlight.astro.build/) and needs **Node ≥ 22.12**
(CI pins 24) beside the .NET SDK — the one non-.NET toolchain dependency in the
repository. The content stays in `docs/`; only the site machinery lives in `docs/site/`.

```
cd docs/site
npm ci          # first time, or after package-lock.json changes
npm run dev     # preview at http://localhost:4321
npm run build   # -> docs/site/dist, then validates every link and image
```

`npm run build` is `astro build && node check-links.mjs`. The checker resolves every
`href` and `src` in the emitted HTML against the emitted files exactly as a browser
would, and checks `#fragments` against the ids actually present — so a broken cross-page
link, a missing screenshot or a renamed heading fails the build instead of going
unnoticed.

The **API reference** is generated separately by DocFX and published as a static subtree
at `/api/`:

```
dotnet tool restore
dotnet docfx docs/docfx.json    # -> docs/_apisite
```

CI (`.github/workflows/docs.yml`) runs the whole sequence — build, DocsGen, docfx, the
Astro build, the WebAssembly demo publish — merges the three trees into one `_site`
(`/`, `/api/`, `/live/`) and deploys it to GitHub Pages on every push to `main`.
