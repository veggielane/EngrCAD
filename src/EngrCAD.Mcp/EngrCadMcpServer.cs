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
    /// <paramref name="viewerTools"/>, when given, appends the live-viewer bridge tools
    /// (the MCP process was told where a running window's remote-control endpoint
    /// listens) — a plain headless session never advertises tools it cannot honor.
    /// </summary>
    public static McpServerOptions BuildOptions(
        SceneTools tools, string title = "EngrCAD", ViewerTools? viewerTools = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in BuildTools(tools))
            toolCollection.Add(tool);
        if (viewerTools is not null)
        {
            foreach (var tool in BuildViewerTools(viewerTools))
                toolCollection.Add(tool);
        }

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
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.ListTabs,
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
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.ListParts,
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
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.DescribePart,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.Screenshot, new McpServerToolCreateOptions
            {
                Name = "screenshot",
                Title = "Render the model",
                Description = "Renders the model headlessly and returns a PNG image — use it to "
                            + "SEE the design. Supports the standard CAD views (iso, front, back, "
                            + "left, right, top, bottom) or an explicit camera (cameraYaw/"
                            + "cameraPitch in degrees + cameraDistance/cameraTarget, or cameraEye), "
                            + "the display styles (shaded-edges, shaded, wireframe, points), one "
                            + "axis section plane (sectionAxis + sectionOffset) or up to four "
                            + "general planes (sectionPlanes + sectionCombine — two perpendicular "
                            + "planes make the classic quarter cutaway) so interiors, bores and "
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
                            + "render; width/height set the image size). Writes to the filesystem.",
                ReadOnly = false,
                Destructive = true,
                Idempotent = true,
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.Export,
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
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.Regeneration,
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
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.Regeneration,
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
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.Regeneration,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.SaveDocument, new McpServerToolCreateOptions
            {
                Name = "save_document",
                Title = "Save the document",
                Description = "Writes the whole model — tabs, parts with their feature "
                            + "histories, assemblies, mates, annotations and results — to one "
                            + "JSON document file. This is how session edits SURVIVE the "
                            + "session: set_param and the suppression tools change the running "
                            + "model only, and saving hands that tuning back to the user as a "
                            + "file they can reopen. A document is its construction history, so "
                            + "history-backed parts reload parametric; parts with no recipe (a "
                            + "raw mesh, an imported STL, an Sdf) embed a mesh snapshot and are "
                            + "named in the result. Writes to the filesystem.",
                ReadOnly = false,
                Destructive = true,
                Idempotent = true,
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.SaveDocument,
                OpenWorld = false,
            }),

            McpServerTool.Create(tools.LoadDocument, new McpServerToolCreateOptions
            {
                Name = "load_document",
                Title = "Load a document",
                Description = "Reads a document written by save_document and makes it the "
                            + "session's model; history-backed parts regenerate, so it is "
                            + "parametric again and every other tool works on it. Pass "
                            + "adopt=false to read and report without changing anything. reload "
                            + "still re-runs the design program's own source and discards the "
                            + "loaded document — a file is a session-lifetime overlay, not a new "
                            + "truth. Records this build cannot rebuild come back as warnings, "
                            + "never as a failure.",
                ReadOnly = false,
                Destructive = true,
                Idempotent = true,
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.LoadDocument,
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
                UseStructuredContent = true,
                OutputSchema = ToolSchemas.Reload,
                OpenWorld = false,
            }),
        ];
    }

    /// <summary>
    /// The live-viewer bridge tools, forwarding to a running window's remote-control
    /// endpoint (see <c>RemoteControl.cs</c> in EngrCAD.Viewer). Named without a prefix
    /// where no headless tool collides; the frame capture is <c>viewer_screenshot</c>
    /// because <c>screenshot</c> is the headless render.
    /// </summary>
    public static IReadOnlyList<McpServerTool> BuildViewerTools(ViewerTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return
        [
            McpServerTool.Create(tools.SetView, new McpServerToolCreateOptions
            {
                Name = "set_view",
                Title = "Set the viewer's view",
                Description = "Snaps the RUNNING viewer window to a standard view (iso, front, "
                            + "back, left, right, top, bottom) — the toolbar buttons, remotely. "
                            + "Distance and target are kept.",
                ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
            }),
            McpServerTool.Create(tools.Fit, new McpServerToolCreateOptions
            {
                Name = "fit",
                Title = "Fit the viewer's camera",
                Description = "Zoom-to-fit the running viewer on its visible parts.",
                ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
            }),
            McpServerTool.Create(tools.SetSection, new McpServerToolCreateOptions
            {
                Name = "set_section",
                Title = "Toggle the viewer's section cut",
                Description = "Turns the running viewer's axis-aligned section cut on or off, "
                            + "optionally choosing the axis (x/y/z) and plane offset.",
                ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
            }),
            McpServerTool.Create(tools.SetViewStyle, new McpServerToolCreateOptions
            {
                Name = "set_view_style",
                Title = "Set the viewer's global style",
                Description = "Sets the running viewer's global view style: shaded-edges, shaded, "
                            + "wireframe, or points (parts with explicit display modes still win).",
                ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
            }),
            McpServerTool.Create(tools.SetDisplayMode, new McpServerToolCreateOptions
            {
                Name = "set_display_mode",
                Title = "Set one part's display mode",
                Description = "Draws one part shaded, wireframe, or translucent in the running "
                            + "viewer (by occurrence path, as list_parts reports them).",
                ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
            }),
            McpServerTool.Create(tools.SelectPart, new McpServerToolCreateOptions
            {
                Name = "select_part",
                Title = "Select a part in the viewer",
                Description = "Selects a part by occurrence path in the running viewer (gold "
                            + "highlight + title bar), or clears the selection when no part is given.",
                ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
            }),
            McpServerTool.Create(tools.GetSelection, new McpServerToolCreateOptions
            {
                Name = "get_selection",
                Title = "Read the viewer's selection",
                Description = "The occurrence path the user (or a previous select_part) has "
                            + "selected in the running viewer — how an assistant learns what "
                            + "'this part' means.",
                ReadOnly = true, OpenWorld = false,
            }),
            McpServerTool.Create(tools.Measure, new McpServerToolCreateOptions
            {
                Name = "measure",
                Title = "Measure in the viewer",
                Description = "Picks two surface points at viewport coordinates (DIPs) in the "
                            + "running viewer, shows the transient dimension there, and returns "
                            + "both world points and their distance.",
                ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
            }),
            McpServerTool.Create(tools.Screenshot, new McpServerToolCreateOptions
            {
                Name = "viewer_screenshot",
                Title = "Capture the viewer window",
                Description = "Asks the running viewer to save its NEXT rendered frame as a PNG "
                            + "(the window's own capture path — GL is only touched inside the "
                            + "render pass). The headless render is the separate screenshot tool.",
                ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
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
        CancellationToken cancellationToken = default) =>
        await RunAsync(input, output, tools, title, viewerTools: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary><see cref="RunAsync(Stream, Stream, SceneTools, string, CancellationToken)"/>
    /// plus the optional live-viewer bridge tools.</summary>
    public static async Task RunAsync(
        Stream input, Stream output, SceneTools tools, string title,
        ViewerTools? viewerTools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(tools);

        // StreamServerTransport (the base of the SDK's StdioServerTransport) is used for
        // both the real process and the in-process tests, so there is exactly one server
        // code path — and it lets the caller hand us the stdout stream it captured
        // BEFORE Console.Out was redirected.
        await using var transport = new StreamServerTransport(input, output, serverName: title);
        await using var server = McpServer.Create(transport, BuildOptions(tools, title, viewerTools));
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
