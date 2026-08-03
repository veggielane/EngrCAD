# EngrCAD.DocsGen

Executes the C# snippets embedded in `docs/**/*.md` and renders their scenes to PNGs,
so every example in the documentation is compiled, run, and screenshotted by the
build — examples cannot drift from the code.

- Convention (full contract in `docs/writing-examples.md`): fences tagged
  ` ```csharp render:<id> ` (must define `Scene scene`; rendered to
  `docs/examples/images/<id>.png`) or ` ```csharp run:<id> ` (executed only).
- Snippets run as Roslyn C# scripts referencing the kernel assemblies, with the
  EngrCAD namespaces imported and a `Scratch` temp-directory global for file output.
- Exit code is nonzero on any compile/run failure, missing `scene`, duplicate id, or
  unreferenced image. If offscreen GL is unavailable (`EngrCad.CanRenderToImage`
  false), rendering is skipped with a warning and the committed PNGs are kept —
  correctness failures still fail the build.

```
dotnet run --project tools/EngrCAD.DocsGen -- docs [--images <dir>] [--no-render]
```

The docs root is an argument, so the generator does not care where the markdown lives;
it scans everything under it except the generated trees (`api/`, `_apisite/`, and the
site's `node_modules/`, `dist/`, `.astro/`).

Used locally and by `.github/workflows/docs.yml`, which runs it before building the
Astro Starlight site (`docs/site/`) and the DocFX API reference (`docs/docfx.json`).
