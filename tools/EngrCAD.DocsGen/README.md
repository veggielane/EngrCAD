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

## Live examples

Each rendered snippet is compiled a **second** time — against exactly the assemblies
`EngrCAD.Web` ships, with no globals — and emitted as a standalone assembly the
WebAssembly viewer loads on demand, so an example page's screenshot can be swapped for
the kernel building that model in the reader's tab (`LiveExamples.cs`; the loading half is
`EngrCAD.Web`'s `LiveExample`, and the round trip between them is pinned by
`tests/EngrCAD.DocsGen.Tests`).

**The reference set is the rule.** "Can a reader run this?" is answered by the C#
compiler rather than by a maintained list: a snippet reaching for `EngrCAD.Fea`, for the
desktop viewer, or for the `Scratch` global does not compile there and the refusal carries
the compiler's own words. The one thing a reference set cannot catch is code that compiles
and then fails on the browser's EMPTY filesystem, which is a short named list resolved
through the **semantic** model — `heightmaps.md` names `Heightmap.ReadPng` in a comment
while being entirely procedural, and a text scan refuses it wrongly.

Assemblies go to `samples/EngrCAD.WebDemo/wwwroot/examples/` (build output, gitignored).
The committed artifact is `docs/examples/live-examples.json`, which the site reads to
decide which screenshot gets a Run button — deterministic on purpose, so it is not dirty
after every run, which is why it carries no timings or byte counts.

```
dotnet run --project tools/EngrCAD.DocsGen -- docs
      [--images <dir>] [--no-render] [--live <dir>] [--no-live]
```

The docs root is an argument, so the generator does not care where the markdown lives;
it scans everything under it except the generated trees (`api/`, `_apisite/`, and the
site's `node_modules/`, `dist/`, `.astro/`).

Used locally and by `.github/workflows/docs.yml`, which runs it before building the
Astro Starlight site (`docs/site/`) and the DocFX API reference (`docs/docfx.json`).
