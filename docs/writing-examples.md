# Writing documentation examples

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

`EngrCAD.Viewer` is imported for exactly these types. A variable of the right name but
the **wrong type is an error**, never a silent miss — an example that quietly ignored
its own section plane would be a trap. See
[the viewer page](examples/viewer.md#several-planes-and-oblique-ones) for a worked
oblique quarter cut.

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

- Scans `docs/**/*.md` (excluding `_site/` and `api/`), executes every tagged
  snippet in file order, writes PNGs to `docs/examples/images/<id>.png`.
- **Exit code is nonzero** if any snippet fails to compile or run, a `render:`
  snippet defines no `scene`, an id is duplicated, or a page never references its
  generated image.
- On machines where offscreen GL is unavailable (`EngrCad.CanRenderToImage == false`,
  e.g. a bare CI runner), rendering is skipped with a warning and the **committed**
  PNGs are used — execution failures still fail the build. Generated PNGs are
  committed to the repository for exactly this reason.

## Building the site

```
dotnet tool restore
dotnet docfx docs/docfx.json          # metadata (API reference) + static site -> docs/_site
dotnet docfx docs/docfx.json --serve  # preview at http://localhost:8080
```

CI (`.github/workflows/docs.yml`) runs the same three steps — build, DocsGen,
docfx — and deploys `docs/_site` to GitHub Pages on every push to `main`.
