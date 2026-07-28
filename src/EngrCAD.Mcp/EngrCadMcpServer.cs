using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace EngrCAD.Mcp;

/// <summary>
/// Builds and runs the MCP server for a design program's scene. All behavior lives in
/// <see cref="SceneTools"/>; this file only names, describes, and wires the tools, so
/// the tool surface can be tested without a transport.
/// </summary>
public static class EngrCadMcpServer
{
    /// <summary>The server name and version reported at initialize.</summary>
    public static string Version { get; } =
        typeof(EngrCadMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? typeof(EngrCadMcpServer).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private const string Instructions = """
        This server exposes ONE CAD model — the design program it was launched from —
        built with the EngrCAD kernel. The model is a Scene of named tabs holding parts
        (each part is exact B-Rep, an implicit SDF, or a mesh).

        Start with list_parts to see what exists, then screenshot to look at it (that
        returns a real rendered image, so use it whenever a question is about shape,
        proportion, or whether something looks right). describe_part gives the
        measurable facts — volume, surface area, bounding box — plus the construction
        tree, which is how the part was built, step by step.

        Parts built from a parametric feature history (their construction tree lists
        features with parameter values) can be DRIVEN: set_param edits a [Param] value
        and regenerates, suppress_feature/unsuppress_feature toggle a feature. These
        edits live in the running session only — the program's source is the truth, so
        to change the design permanently, edit its source; reload re-runs the scene
        factory (discarding session edits) so source edits show up without restarting.
        """;

    /// <summary>
    /// The protocol options for a server over <paramref name="tools"/>: server identity,
    /// instructions, the tool collection, and the <c>engrcad://scene</c> resource.
    /// </summary>
    public static McpServerOptions BuildOptions(SceneTools tools, string title = "EngrCAD")
    {
        ArgumentNullException.ThrowIfNull(tools);

        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in BuildTools(tools))
            toolCollection.Add(tool);

        var resources = new McpServerResourceCollection
        {
            McpServerResource.Create(
                tools.SceneJson,
                new McpServerResourceCreateOptions
                {
                    UriTemplate = "engrcad://scene",
                    Name = "scene",
                    Title = $"{title}: scene summary",
                    Description = "The whole document as JSON: tabs, their parts, each part's "
                                + "geometry kind and whether it has an exact B-Rep route. Cheap "
                                + "(nothing is tessellated to answer it).",
                    MimeType = "application/json",
                }),
        };

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "engrcad",
                Title = title,
                Version = Version,
                Description = "A live EngrCAD design program, queryable and renderable.",
            },
            ServerInstructions = Instructions,
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability(),
                Resources = new ResourcesCapability(),
            },
            ToolCollection = toolCollection,
            ResourceCollection = resources,
        };
    }

    /// <summary>The v1 tool surface, in the order a client should discover it.</summary>
    public static IReadOnlyList<McpServerTool> BuildTools(SceneTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return
        [
            McpServerTool.Create(tools.ListTabs, new McpServerToolCreateOptions
            {
                Name = "list_tabs",
                Title = "List tabs",
                Description = "The model's tabs (the viewer's tab strip) with how many parts, "
                            + "assemblies, and placed instances each holds. Costs nothing — no "
                            + "geometry is evaluated.",
                ReadOnly = true,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.ListParts, new McpServerToolCreateOptions
            {
                Name = "list_parts",
                Title = "List parts",
                Description = "Every distinct part with the facts that are free: name, tab, "
                            + "geometry kind (Shape / B-Rep / mesh / SDF), how many times it is "
                            + "placed and under which occurrence paths, display mode, colour, "
                            + "annotation count, and whether it has an exact B-Rep route "
                            + "(STEP-exportable). Start here. For volumes, areas and bounds — "
                            + "which require tessellation — call describe_part on one part.",
                ReadOnly = true,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.DescribePart, new McpServerToolCreateOptions
            {
                Name = "describe_part",
                Title = "Describe a part",
                Description = "One part in full: geometry kind, triangle/vertex counts, whether "
                            + "the mesh is closed, volume, surface area, local and world bounding "
                            + "boxes, placement, annotations, and the construction tree — the "
                            + "ordered record of how the part was built (booleans, drills, "
                            + "fillets, sketches; or the parametric feature list with its "
                            + "parameter values). This is the only listing tool that tessellates, "
                            + "and it tessellates just the named part.",
                ReadOnly = true,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.Screenshot, new McpServerToolCreateOptions
            {
                Name = "screenshot",
                Title = "Render the model",
                Description = "Renders the model headlessly and returns a PNG image — use it to "
                            + "SEE the design. Supports the standard CAD views (iso, front, back, "
                            + "left, right, top, bottom), the display styles (shaded-edges, "
                            + "shaded, wireframe, points), and a section plane (sectionAxis + "
                            + "sectionOffset) that cuts the model open so interiors, bores and "
                            + "wall thicknesses are visible. Narrow to one tab or part when a "
                            + "scene is busy. Needs a GPU/ANGLE context; if that is missing it "
                            + "returns an error and every other tool keeps working.",
                ReadOnly = true,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.Export, new McpServerToolCreateOptions
            {
                Name = "export",
                Title = "Export to a file",
                Description = "Writes the model to a file the caller names. Format follows the "
                            + "extension: .step (exact B-Rep, one file per part — the CAD "
                            + "interchange format), .stl or .obj (meshes, instances merged with "
                            + "their transforms — for slicers and 3D printing), or .png (a "
                            + "render). Writes to the filesystem.",
                ReadOnly = false,
                Destructive = true,
                Idempotent = true,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.SetParam, new McpServerToolCreateOptions
            {
                Name = "set_param",
                Title = "Set a feature parameter",
                Description = "Edits one [Param] value on a feature of a history-backed part "
                            + "(the parts whose construction tree lists features) and regenerates "
                            + "the model. The result is the regeneration report: per-feature "
                            + "outcomes and timings. A failed regeneration keeps the part's "
                            + "previous geometry and names the failing feature; the edit stays "
                            + "applied so it can be corrected and regenerated. reload discards "
                            + "these edits — the program's source is still the truth.",
                ReadOnly = false,
                Destructive = false,
                Idempotent = true,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.SuppressFeature, new McpServerToolCreateOptions
            {
                Name = "suppress_feature",
                Title = "Suppress a feature",
                Description = "Suppresses a feature of a history-backed part (it passes the body "
                            + "through untouched — a hole feature's bores disappear) and "
                            + "regenerates. Same result shape and failure semantics as set_param.",
                ReadOnly = false,
                Destructive = false,
                Idempotent = true,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.UnsuppressFeature, new McpServerToolCreateOptions
            {
                Name = "unsuppress_feature",
                Title = "Unsuppress a feature",
                Description = "Re-enables a suppressed feature and regenerates (the inverse of "
                            + "suppress_feature).",
                ReadOnly = false,
                Destructive = false,
                Idempotent = true,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.Reload, new McpServerToolCreateOptions
            {
                Name = "reload",
                Title = "Reload the model",
                Description = "Re-runs the design program's scene factory and swaps the result in "
                            + "— the headless equivalent of the viewer's hot reload. Call it after "
                            + "the model's source has changed. If the model throws, the previous "
                            + "scene stays and the error is reported.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
            }),
        ];
    }

    /// <summary>
    /// Serves MCP over the given streams until the client disconnects or
    /// <paramref name="cancellationToken"/> fires. <paramref name="output"/> must carry
    /// protocol frames and nothing else — see <see cref="StdoutGuard"/>.
    /// </summary>
    public static async Task RunAsync(
        Stream input, Stream output, SceneTools tools, string title = "EngrCAD",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(tools);

        // StreamServerTransport (the base of the SDK's StdioServerTransport) is used for
        // both the real process and the in-process tests, so there is exactly one server
        // code path — and it lets the caller hand us the stdout stream it captured
        // BEFORE Console.Out was redirected.
        await using var transport = new StreamServerTransport(input, output, serverName: title);
        await using var server = McpServer.Create(transport, BuildOptions(tools, title));
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
