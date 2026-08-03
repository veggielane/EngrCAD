# Shared EngrCAD engineering context (referenced by every agent definition)

This file is not an agent. Agent definitions tell you to read it first — do so, then
read `CLAUDE.md`, `design.md`, and the `README.md` of the project you own.

## Environment
- .NET SDK 10 is user-local. EVERY shell command needs this preamble:
  `$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"; $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH";`
- Build: `dotnet build EngrCAD.slnx -v q --nologo` · Test: `dotnet test EngrCAD.slnx --nologo -v q`
- PowerShell 5.1: no `&&`; here-strings for multi-line args; no double quotes inside
  single-quoted here-strings passed to git.
- If builds fail with locked DLLs, a demo is running: `Get-Process -Name "EngrCAD.Demo","EngrCAD.LiveDemo" | Stop-Process -Force`.
- NEVER round-trip source files through PowerShell `Get-Content`/`Set-Content` —
  PS 5.1 reads BOM-less UTF-8 as ANSI and mangles non-ASCII (en dashes → mojibake).
  Use the Edit/Write tools for file content, always.

## Non-negotiable conventions
- Kernel projects (Core, Mesh, Implicit, BRep, Interop, Query, Modeling) must stay
  free of UI/rendering dependencies.
- Math types are `readonly struct`; hot paths allocate nothing; never compare floats
  with `==` — use the `Tolerance` policy in EngrCAD.Core.
- Every geometric algorithm gets tolerance-aware tests against **analytic ground
  truth** where it exists (exact volumes, Pappus, known areas) and brute force where
  it doesn't. Derive test tolerances from the discretization, not magic numbers.
- Weld tolerance is 1e-9 ABSOLUTE: geometry that must weld (tessellation seams,
  band junctions) must be constructed exactly, not via finite differences or
  projections (both carry ~1e-6..1e-9 error — this has caused real cracks; see the
  numerical notes in CLAUDE.md).
- File-scoped namespaces, `EngrCAD.*` root namespaces, xUnit, C# latest.

## Working rules for backlog agents
- `todo.md` is the backlog; many items name geometry3Sharp classes at
  `C:\Users\chris\projects\git\geometry3Sharp` worth reading before implementing.
- Work ONLY within your assigned project(s) and their test project(s) unless your
  task explicitly says otherwise. Do NOT edit `CLAUDE.md`, `design.md`, `todo.md`,
  or other projects' files — instead, end your report with the exact doc/backlog
  updates the integrator should make.
- **Documentation is part of the feature — not optional.** When your change alters
  behavior or adds API:
  1. Update the owning project's `README.md` in the same commit (this is in your
     domain and REQUIRED, not optional).
  2. If the feature is user-facing (new `Shape` ops, viewer features, exports,
     standards catalogs): check `docs/` — the Astro Starlight site with executable
     examples. If you may edit `docs/`, add/extend the example page (YAML `title:`
     frontmatter, a code fence tagged ` ```csharp render:<id> ` per
     `docs/writing-examples.md`, an entry in `docs/site/astro.config.mjs`'s sidebar,
     verified by running `dotnet run --project tools/EngrCAD.DocsGen -- docs`). If docs/ is outside
     your assignment, your final report MUST name the exact docs pages/examples
     the integrator needs to add — a feature without docs is not done.
  3. XML doc comments on all new public API (they feed the generated API reference).
- Before finishing: build the WHOLE solution and run the WHOLE test suite; all
  existing tests must stay green. New features need new tests.
- If you are in a git worktree, commit your finished work there (`git add -A` +
  a descriptive commit message ending with your agent name) and report the branch
  name (`git branch --show-current`) and worktree path.
- Debug scripts: use `dotnet run script.cs` files with a
  `#:project <path-to-csproj>` first line; put them in a `scratch/` folder you
  delete before committing.
