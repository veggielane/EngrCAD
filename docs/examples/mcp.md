# AI assistants (MCP)

A design program can expose itself to an AI assistant over the
[Model Context Protocol](https://modelcontextprotocol.io) — headlessly, with no
window. The assistant can then list the model's parts, measure them, read how they
were built, export them, and **see** them: the `screenshot` tool renders the scene and
returns the PNG as an image, so questions about shape and proportion get answered by
looking rather than guessing.

Swap the entry point from `EngrCad.Run` to `EngrCadMcp.Run` (package
[`EngrCAD.Mcp`](https://www.nuget.org/packages/EngrCAD.Mcp)):

```csharp
using EngrCAD.Mcp;
using EngrCAD.Modeling;

return EngrCadMcp.Run(args, BuildScene, "bracket");

static Scene BuildScene()
{
    var scene = new Scene();
    scene.Add(new Part("bracket",
        Shape.Extrude(Sketch.RoundedRectangle(40, 24, 5), 8)
            .Drill(StandardHoles.Clearance(5), [new(-14, 0), new(14, 0)], depth: 10,
                   SketchPlane.At((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY))));
    return scene;
}
```

That adds one switch, `--mcp`. Everything else is untouched: no arguments still opens
the live `dotnet watch` loop, and `--view`, `--export`, `--render`, `--section`,
`--render-style` behave exactly as before.

```
dotnet run --project MyModel -- --mcp     # serve this model over MCP on stdio
dotnet run --project MyModel              # unchanged: the live viewer loop
```

## Configuring a client

MCP clients launch the server as a child process and talk to it over stdin/stdout:

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

For a faster start-up (no build check per launch), point it at a published binary
instead:

```json
{
  "mcpServers": {
    "bracket": {
      "command": "C:/models/bracket/bracket.exe",
      "args": ["--mcp"]
    }
  }
}
```

## What the assistant gets

| Tool | Answers |
| --- | --- |
| `list_tabs` | What tabs exist, and how much is in each. |
| `list_parts` | Every part: name, tab, geometry kind (Shape / B-Rep / mesh / SDF), how many times it is placed and where, colour, display mode, whether it has an exact B-Rep route. |
| `describe_part` | Faces, vertices, closed, volume, surface area, local and world bounding boxes, placement, annotations — and the **construction tree**, the ordered record of how the part was built (booleans, drills, fillets, sketches; or the parametric feature list with its `[Param]` values). |
| `screenshot` | A rendered PNG. Standard views (`iso`, `front`, `back`, `left`, `right`, `top`, `bottom`), display styles (`shaded-edges`, `shaded`, `wireframe`, `points`), a section plane (`sectionAxis` + `sectionOffset`) that cuts the model open to show bores and wall thickness, image size, an optional `tab`/`part` filter, and `t` — a position on the program's [animation](animation.md) timeline, so an assistant can ask for the mechanism at half stroke. |
| `export` | Writes `.step` (exact B-Rep, one file per part), `.stl` or `.obj` (meshes merged with instance transforms), or `.png`. |
| `reload` | Re-runs the scene factory after the model's source changed — the headless equivalent of hot reload. A model that throws leaves the previous scene in place and reports the error. |

There is also a resource, `engrcad://scene`: the whole document as JSON, cheap enough
to read on every turn.

The server is **read-only** on the design: to change the model, edit its source and
call `reload`.

## Two things worth knowing

**stdout is the protocol.** The stdio transport *is* standard output, so a single
`Console.WriteLine` in model code would corrupt the stream and break the client.
`--mcp` mode redirects `Console.Out` to stderr before the scene factory ever runs and
keeps the raw stdout handle for protocol frames, so printing from a model is safe —
it shows up in the client's server log instead.

**Nothing is meshed up front.** Tessellating a busy scene costs tens of seconds, and
most questions need no geometry: `list_tabs`, `list_parts` and `reload` evaluate none
at all, `describe_part` meshes only the part it was asked about, and `screenshot` and
`export` mesh only what they are about to draw or write.

## Hosting it yourself

The pieces are separable, so the tools can be embedded in a larger server or driven
over another transport:

```csharp
var session = new SceneSession(BuildScene);       // the live scene, plus reload
var tools = new SceneTools(session);              // one method per tool
var options = EngrCadMcpServer.BuildOptions(tools, "bracket");

await EngrCadMcpServer.RunAsync(input, output, tools, "bracket", cancellationToken);
```
