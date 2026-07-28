# EngrCAD.Mcp

Turns a design program into an **MCP server**: an AI assistant can list the model's
parts, measure them, read how they were built — and *see* them, because the
`screenshot` tool returns a real rendered PNG.

```csharp
using EngrCAD.Mcp;

return EngrCadMcp.Run(args, BuildScene, "my bracket");

static Scene BuildScene() { ... }
```

`EngrCadMcp.Run` adds exactly one switch, `--mcp`, and hands every other argument to
`EngrCad.Run` — so `--view`, `--export`, `--render` and the `dotnet watch` live loop
are unchanged. With `--mcp` the program opens no window; it speaks the
[Model Context Protocol](https://modelcontextprotocol.io) over stdio.

Configure an assistant with:

```json
{
  "mcpServers": {
    "bracket": {
      "command": "dotnet",
      "args": ["run", "--project", "samples/MyModel", "--", "--mcp"]
    }
  }
}
```

## Why this is a separate package

Every `src/*` project packs to NuGet. A protocol stack is not something a viewer
consumer asked for, so the SDK dependency lives here and nowhere else: reference
`EngrCAD.Mcp` and you get it, ignore it and the kernel and viewer are exactly as they
were. This project references `EngrCAD.Viewer` (for the offscreen renderer) and
[`ModelContextProtocol.Core`](https://www.nuget.org/packages/ModelContextProtocol.Core)
— the official C# SDK's protocol-and-transports package, deliberately not the
`ModelContextProtocol` metapackage, which drags in `Microsoft.Extensions.Hosting`.
This is a library entry point, not a generic host.

## The tools

| Tool | What it does | Cost |
| --- | --- | --- |
| `list_tabs` | Tabs with part/assembly/instance counts. | free |
| `list_parts` | Every distinct part: name, tab, geometry kind, occurrence paths, display mode, colour, annotation count, whether it has an exact B-Rep route. | free |
| `describe_part` | One part in full: faces, vertices, closed, volume, surface area, local and world bounds, placement, annotations, and the **construction tree** (`Part.ConstructionTree()` — how the part was built, step by step). | meshes that one part |
| `screenshot` | Renders and returns a **PNG image block**. Standard views (iso/front/back/left/right/top/bottom) **or an explicit camera** (`cameraYaw`/`cameraPitch` in degrees + `cameraDistance`/`cameraTarget`, or `cameraEye` — the orbit camera is Z-up, no roll), display styles (shaded-edges/shaded/wireframe/points), one axis section plane (`sectionAxis` + `sectionOffset`) **or up to 4 general planes** (`sectionPlanes` as `[nx, ny, nz, offset]` rows + `sectionCombine` intersection/union — two perpendicular planes are the classic quarter cutaway), size, and an optional tab/part filter. | meshes what it renders |
| `export` | Writes `.step` (exact B-Rep, one file per part), `.stl`/`.obj` (merged with instance transforms), or `.png` (`width`/`height` set the image size). | meshes what it writes |
| `set_param` | Edits one `[Param]` value on a feature of a history-backed part and **regenerates**. The result is the regeneration report (per-feature applied/cached/suppressed/failed/skipped with timings). A failed regeneration keeps the part's previous geometry and names the failing feature; the edit stays applied so it can be corrected — `FeatureHistory`'s own validation-first / failure-keeps-prefix semantics, surfaced verbatim. | regenerates (no meshing) |
| `suppress_feature` / `unsuppress_feature` | Toggles a feature's suppression (a suppressed feature passes the body through untouched — a hole feature's bores disappear) and regenerates. Same result shape as `set_param`. | regenerates (no meshing) |
| `reload` | Re-invokes the scene factory — the headless equivalent of hot reload. A model that throws leaves the previous scene in place. **Discards session edits**: the program's source is the truth. | free |

Plus one resource, `engrcad://scene`: the whole document as JSON (tabs, parts,
geometry kinds), cheap enough to read on every turn.

Failures come back as `isError` results with a readable message — "No part named
'flage'. Parts in this scene: Model/flange, …" — never as protocol errors. An
assistant should be able to correct itself and carry on.

**Results are structured content.** Every JSON-returning tool declares an output
schema (`ToolSchemas.cs`, wired via the SDK's `UseStructuredContent` +
`OutputSchema` — the explicit-schema form, because the tool methods return
`CallToolResult` directly) and populates `structuredContent`, so clients consume
typed JSON without parsing text blocks. The pretty-printed text block still rides
along for older clients; one `JsonObject` feeds both, so they cannot disagree.
`screenshot` is the deliberate exception — its result is an image content block,
which structured content does not model.

## stdout is the protocol

The stdio transport *is* standard output. One stray `Console.WriteLine` — from the
design program, from a library, from `EngrCad.Run`'s own "wrote part.step" reporting —
lands in the middle of the frame stream and every connected client breaks.

`StdoutGuard` handles it. On `--mcp` the server takes the real stdout **handle** for
protocol frames and then points `Console.Out` at **stderr**, so anything written
through `Console.Write*` anywhere in the process afterwards goes to stderr (where MCP
clients surface it as server logging). The scene factory is invoked *after* that
redirection, so a model that prints while it builds is safe. The `ILogger` is pointed
at `EngrCadLoggers.StandardError` too when the caller has not configured one — the
default console logger resolves `Console.Out` on every call, so it already follows the
guard, but naming the stderr sink means a future default cannot quietly undo this.

The one thing this cannot defend against is code that opens the standard-output handle
itself or writes to file descriptor 1 natively. Nothing in EngrCAD does.

`StdioSessionTests` locks the rule: it launches a real child process whose model
deliberately prints while building, drives a full JSON-RPC session over its
stdin/stdout, and asserts that **every** stdout line parses as a JSON-RPC frame and
that the noise turned up on stderr.

## Laziness

Meshing a busy scene costs tens of seconds and most tools need no geometry at all, so
nothing is tessellated at startup:

- `list_tabs`, `list_parts`, `reload` and the scene resource evaluate **no** geometry
  (locked by a test using an evaluation-counting SDF).
- `describe_part` meshes **only the part named**.
- `screenshot` and `export` mesh only what they are about to draw or write: the whole
  scene goes through `Scene.PreMesh` (so it inherits that routine's parallelism), while
  a tab- or part-scoped call meshes just those instances.

Meshes cache on the `Part`, so the second call is free; a `reload` builds a new scene
and starts over, which is correct — the geometry changed.

## Hosting it yourself

`EngrCadMcp.Serve(factory, options)` runs the server on this process's stdio with the
guard in place. For a different transport, or to embed the tools in a larger server,
the pieces are separable:

```csharp
var session = new SceneSession(BuildScene);      // the live scene + reload
var tools = new SceneTools(session);             // one method per tool, returns CallToolResult
var options = EngrCadMcpServer.BuildOptions(tools, "my bracket");
await EngrCadMcpServer.RunAsync(input, output, tools, "my bracket", cancellationToken);
```

`SceneTools` methods are ordinary C# methods returning the protocol's
`CallToolResult`, which is why the tool tests need no client, transport, or process at
all.

## Parametric editing

The write tools work on **history-backed parts** — parts created from a
`FeatureHistory` (`history.ToPart(...)` or `new Part(name, history)`); `list_parts`
marks them with `hasConstructionTree` and `describe_part` shows the feature list with
current parameter values. There is no separate registration seam: `Part.History` *is*
the seam, so a design that already builds parts from histories is editable with no
extra wiring. `set_param` goes through the same JSON conversion as
`FeatureHistory.SaveParameters`/`LoadParameters`, so the accepted value spellings
cannot drift between the parameter file and the tool. A successful edit bumps the
session `generation`, telling clients their earlier reads are stale; `Part.Regenerate`
then clears the part's cached mesh/solid/edges/annotations so every later tool sees
the edited model. Edits live in the running session only — `reload` re-runs the
program's source and discards them.

## Known limits (v1)

- The named-view poses route through the shared `ViewCubeMath.PoseFor` /
  `CameraMath.FrameDistance` in `EngrCAD.Viewer.Core` (`NamedViews.cs` is only the
  name table), so the toolbar, the view cube, the browser client and `screenshot`
  cannot disagree about what "Front" means.
