using System.ComponentModel;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace EngrCAD.Mcp;

/// <summary>
/// The client half of the viewer's remote-control endpoint (todo.md option (b)): the
/// MCP server runs as a separate process and bridges tool calls to a RUNNING viewer
/// window over loopback JSON-RPC. One connection per request, deliberately — the
/// viewer may be restarted (dotnet watch rude edits) between calls, and a stateless
/// client reconnects for free instead of holding a broken socket.
/// </summary>
public sealed class ViewerRpcClient(int port, string? token = null)
{
    /// <summary>Where the viewer listens (always 127.0.0.1 — the endpoint is loopback-only).</summary>
    public int Port { get; } = port;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private int _nextId;

    /// <summary>Sends one JSON-RPC request and returns its result node. Throws
    /// <see cref="ViewerRpcException"/> with the server's message on an error
    /// response, and <see cref="SocketException"/>/<see cref="IOException"/> when no
    /// viewer is listening.</summary>
    public async Task<JsonNode?> SendAsync(
        string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, Port, cts.Token).ConfigureAwait(false);
        var stream = client.GetStream();

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _nextId),
            ["method"] = method,
        };
        if (parameters is not null)
            request["params"] = parameters;
        if (token is not null)
            request["token"] = token;

        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
        await stream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)
            ?? throw new IOException("the viewer closed the connection without answering");
        var response = JsonNode.Parse(line) as JsonObject
            ?? throw new IOException($"the viewer answered with a non-JSON line: {line}");
        if (response["error"] is JsonObject error)
            throw new ViewerRpcException((string?)error["message"] ?? "unknown viewer error");
        return response["result"];
    }
}

/// <summary>An error response from the viewer's remote-control endpoint (the message
/// is the server's own, which already names what exists).</summary>
public sealed class ViewerRpcException(string message) : Exception(message);

/// <summary>
/// MCP tools that drive a RUNNING viewer window through <see cref="ViewerRpcClient"/>
/// — the live twin of the headless <see cref="SceneTools"/> surface. Wired in only
/// when the MCP process is told where the viewer listens (<c>--viewer &lt;port&gt;</c>),
/// so a plain headless session never advertises tools it cannot honor. Every failure
/// (no viewer, wrong token, unknown part) is an <c>isError</c> result with the
/// endpoint's own message, never a protocol error.
/// </summary>
public sealed class ViewerTools(ViewerRpcClient client)
{
    /// <summary>The client these tools forward through; exposed for hosts/tests.</summary>
    public ViewerRpcClient Client { get; } = client;

    // ---- tools ----

    public CallToolResult SetView(
        [Description("Standard view: iso, front, back, left, right, top, bottom.")] string view) =>
        Forward("set_view", new JsonObject { ["view"] = view },
            _ => $"viewer set to the {view} view");

    public CallToolResult Fit() =>
        Forward("fit", null, _ => "viewer framed to the visible parts");

    public CallToolResult SetSection(
        [Description("Turn the section cut on (default) or off.")] bool enabled = true,
        [Description("Section axis: x, y, or z (default z).")] string? axis = null,
        [Description("Plane position along the axis (omit to keep the viewer's current offset).")]
        double? offset = null)
    {
        var parameters = new JsonObject { ["enabled"] = enabled };
        if (axis is not null)
            parameters["axis"] = axis;
        if (offset is { } o)
            parameters["offset"] = o;
        return Forward("set_section", parameters,
            _ => enabled ? "viewer section cut enabled" : "viewer section cut disabled");
    }

    public CallToolResult SetViewStyle(
        [Description("Global view style: shaded-edges, shaded, wireframe, points.")] string style) =>
        Forward("set_view_style", new JsonObject { ["style"] = style },
            _ => $"viewer style set to {style}");

    public CallToolResult SetDisplayMode(
        [Description("Occurrence path of the part (see list_parts paths).")] string part,
        [Description("Display mode: shaded, wireframe, translucent.")] string mode) =>
        Forward("set_display_mode", new JsonObject { ["part"] = part, ["mode"] = mode },
            _ => $"'{part}' now drawn {mode}");

    public CallToolResult SelectPart(
        [Description("Occurrence path to select; omit to clear the selection.")] string? part = null)
    {
        var parameters = new JsonObject();
        if (part is not null)
            parameters["part"] = part;
        return Forward("select_part", parameters,
            _ => part is null ? "selection cleared" : $"selected '{part}'");
    }

    public CallToolResult GetSelection() =>
        Forward("get_selection", null, result =>
        {
            string? selected = (string?)result?["selected"];
            return selected is null ? "nothing is selected" : $"selected: '{selected}'";
        });

    public CallToolResult Measure(
        [Description("First point, viewport x in DIPs.")] double x1,
        [Description("First point, viewport y in DIPs.")] double y1,
        [Description("Second point, viewport x in DIPs.")] double x2,
        [Description("Second point, viewport y in DIPs.")] double y2) =>
        Forward("measure",
            new JsonObject { ["x1"] = x1, ["y1"] = y1, ["x2"] = x2, ["y2"] = y2 },
            result => (bool?)result?["hit"] == true
                ? string.Create(CultureInfo.InvariantCulture,
                    $"measured {(double)result!["distance"]!:G6} between the picked surface points")
                : "measure: no surface under one of the points");

    public CallToolResult Screenshot(
        [Description("PNG path for the captured window frame (omit for Pictures/EngrCAD).")]
        string? path = null)
    {
        var parameters = new JsonObject();
        if (path is not null)
            parameters["path"] = path;
        return Forward("screenshot", parameters, result =>
            $"viewer will write its next frame to {(string?)result?["path"]} "
            + "(capture happens inside the render pass; the file appears after the next frame)");
    }

    // ---- plumbing ----

    private CallToolResult Forward(string method, JsonObject? parameters, Func<JsonNode?, string> summary)
    {
        try
        {
            var result = Client.SendAsync(method, parameters).GetAwaiter().GetResult();
            var payload = new JsonObject
            {
                ["summary"] = summary(result),
                ["result"] = result?.DeepClone(),
            };
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = payload.ToJsonString() }],
                StructuredContent = System.Text.Json.JsonSerializer.SerializeToElement(payload),
            };
        }
        catch (ViewerRpcException e)
        {
            return Error(e.Message);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        {
            return Error(
                $"No live viewer answered at 127.0.0.1:{Client.Port} ({e.GetType().Name}: {e.Message}). "
                + "Start the model program with --rpc <port> (and pass the same port to this server "
                + "via --viewer <port>).");
        }
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
