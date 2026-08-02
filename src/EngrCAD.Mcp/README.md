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
| `screenshot` | Renders and returns a **PNG image block**. Standard views (iso/front/back/left/right/top/bottom) **or an explicit camera** (`cameraYaw`/`cameraPitch` in degrees + `cameraDistance`/`cameraTarget`, or `cameraEye` — the orbit camera is Z-up, no roll), display styles (shaded-edges/shaded/wireframe/points), one axis section plane (`sectionAxis` + `sectionOffset`) **or up to 4 general planes** (`sectionPlanes` as `[nx, ny, nz, offset]` rows + `sectionCombine` intersection/union — two perpendicular planes are the classic quarter cutaway), size, an optional tab/part filter, **`shading`** (lit/clay/metal — the analytic matcaps, the same `ShadingStyle` the window's toolbar offers), and **`t`** — a timeline position in [0, 1] of the program's animation, so an assistant can ask for "the mechanism at t = 0.3". | meshes what it renders |
| `export` | Writes `.step` (exact B-Rep, one file per part), `.stl`/`.obj` (merged with instance transforms), or `.png` (`width`/`height` set the image size). | meshes what it writes |
| `set_param` | Edits one `[Param]` value on a feature of a history-backed part and **regenerates**. The result is the regeneration report (per-feature applied/cached/suppressed/failed/skipped with timings). A failed regeneration keeps the part's previous geometry and names the failing feature; the edit stays applied so it can be corrected — `FeatureHistory`'s own validation-first / failure-keeps-prefix semantics, surfaced verbatim. | regenerates (no meshing) |
| `suppress_feature` / `unsuppress_feature` | Toggles a feature's suppression (a suppressed feature passes the body through untouched — a hole feature's bores disappear) and regenerates. Same result shape as `set_param`. | regenerates (no meshing) |
| `save_document` | Writes the whole model — tabs, parts with their feature histories, assemblies, mates, annotations, results — as one `Document.Save` JSON file. **This is how session edits survive the session.** Reports which parts had no construction recipe and so went out as mesh snapshots. | meshes parts with no recipe |
| `load_document` | Reads one back and makes it the session's model (history-backed parts regenerate, so it is parametric again); `adopt: false` reads and reports without changing anything. Records this build cannot rebuild come back as warnings, never as a failure. | regenerates |
| `reload` | Re-invokes the scene factory — the headless equivalent of hot reload. A model that throws leaves the previous scene in place. **Discards session edits AND any loaded document**: the program's source is the truth. | free |

Plus one resource, `engrcad://scene`: the whole document as JSON (tabs, parts,
geometry kinds), cheap enough to read on every turn.

## Driving a RUNNING viewer window

Started with `--mcp --viewer <port>` (and `--viewer-token <t>` when the viewer set
one), the server additionally bridges to a live window's remote-control endpoint —
the model program runs separately with `--rpc <port>` (see the EngrCAD.Viewer README)
— adding: `set_view`, `fit`, `set_section`, `set_view_style`, `set_display_mode`,
`select_part`, `get_selection` (how an assistant learns what "this part" the user is
pointing at means), `measure` (two viewport picks, returns the world points and
distance, shows the transient dimension in the window), `set_animation_time`, and
`viewer_screenshot` (the window's own next-frame capture, returned as a PNG **image
block** exactly as the headless `screenshot` does; the headless render stays
`screenshot`).
Without `--viewer` these tools are never advertised — a headless session does not
offer tools it cannot honor. A dead or wrong endpoint is an `isError` naming the
`--rpc` flag; connections are per-request, so a viewer restarted by `dotnet watch`
just gets reconnected to.

**An animation is DRIVEN, not passed as a parameter.** The headless `screenshot` takes a
`t` and re-evaluates `Animation.At(t)` for that instant, which is exact by construction.
A running window cannot work that way: it has its own playback position, and a capture
arrives a frame later — so `set_animation_time` parks the window's transport at `t` and
**pauses** it (reported as `"playing": false`), after which `viewer_screenshot` captures
that instant. Splitting it into a verb also keeps two different failures apart: "this
window has no animation" (the host never called `WithAnimation`) and "the window produced
no frame" want opposite responses, and a `t` parameter on the capture would collapse them
into one message.

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
which structured content does not model. `viewer_screenshot` is the same exception
for the same reason.

**`viewer_screenshot` returns pixels, not a promise.** It used to answer "the viewer
WILL write its next frame to `<path>`", because `ViewportControl.SaveScreenshot` only
*arms* a capture that the render pass performs on its next frame — so the RPC thread
had no edge to wait on and the path was a claim about the future. The endpoint now
completes when the PNG is on disk (see `ViewportControl.CaptureScreenshotAsync`), so
the bridge simply reads the bytes back — legitimate precisely because the endpoint is
loopback-only, which makes "the file the viewer wrote" a file this process can open.
A window that never renders is refused **by name and on a deadline** rather than
hanging the connection; a capture that succeeds but cannot be read back says so *and*
names the path, since the file is still there for a human to open.

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

## Sampling an animation

A model that declares a timeline (`EngrCad.Configure().WithAnimation(scene => …)`)
makes `screenshot`'s `t` parameter live: `screenshot(t: 0.3, view: "front")` renders the
model **posed at that instant**. The posing goes through `EngrCad.PoseAt`, the same seam
the desktop still overload and every export use, so an assistant's screenshot, a scrubbed
viewport and frame ⌊t·N⌋ of an APNG cannot disagree. A named view or explicit camera
still wins over the animation's own camera track, and a tab/part scope narrows the posed
list by part reference afterwards. The animation is built **lazily** (track construction
can mesh for bounds) and discarded on `reload`, so the timeline follows the model. A
model with no animation gets an `isError` naming `WithAnimation` rather than a silently
un-posed picture.

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

**`save_document` is how they get out.** An assistant that has tuned a model to what
the user asked for writes the whole thing — every tab, part, history, assembly, mate,
annotation and result — as one JSON file the user can reopen, and `load_document`
brings it back *parametric*, so the write tools keep working on it. That the pair is
worth having at all is a consequence of `Document`'s design decision that **a document
is its construction history, not its geometry**; the corollary is stated rather than
hidden, because it is the thing a client must know: a part with no recipe (a raw mesh,
an imported STL, an `Sdf`, a `Shape` graph built in code) goes out as a binary-exact
mesh *snapshot*, and both tools name those parts, so nothing discovers later that
editing one changes nothing.

A loaded document is a **session-lifetime overlay**, not a new truth: `reload` still
re-runs the program's own source and discards it. That keeps one rule for the whole
server rather than two ideas of where the model comes from. `adopt: false` reads and
reports without changing anything — the dry run to run first on a file the assistant
did not write itself.

One protocol fact the round-trip test paid to learn: **the server dispatches
requests concurrently**, as MCP allows. A client that edits and then reads must
await the edit's response before sending the read, or the read can observe the
pre-edit model (real assistant clients already work this way; a hand-rolled driver
must too).

The no-GL error path is testable (and forcible) via the `ENGRCAD_NO_GL=1`
environment variable — `screenshot`/`.png` export then return an `isError` result
naming the constraint while every other tool keeps working. The seam exists because
the GL probe is a process-wide `Lazy` over a real EGL context, so a GPU-less
machine can only be simulated in a child process; it doubles as an operational kill
switch for broken drivers.

## Known limits (v1)

- The named-view poses route through the shared `ViewCubeMath.PoseFor` /
  `CameraMath.FrameDistance` in `EngrCAD.Viewer.Core` (`NamedViews.cs` is only the
  name table), so the toolbar, the view cube, the browser client and `screenshot`
  cannot disagree about what "Front" means.
