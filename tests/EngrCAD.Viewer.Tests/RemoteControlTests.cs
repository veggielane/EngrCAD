using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The remote-control TRANSPORT over a real loopback socket, with a stub handler — no
/// window, no Avalonia. What is pinned here: newline framing, the token gate, the
/// JSON-RPC error envelope for every failure class, and multiple connections.
/// </summary>
public class RemoteControlServerTests
{
    private static async Task<JsonObject> Roundtrip(int port, string frame)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(frame + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        string line = (await reader.ReadLineAsync())!;
        return (JsonObject)JsonNode.Parse(line)!;
    }

    private static RemoteControlServer Echo(string? token = null) => new(
        (method, parameters, _) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters?.DeepClone(),
        }),
        port: 0, token);

    [Fact]
    public async Task Dispatches_a_request_and_frames_the_result()
    {
        using var server = Echo();
        int port = server.Start();

        var response = await Roundtrip(port,
            """{"jsonrpc":"2.0","id":7,"method":"ping","params":{"x":1}}""");

        Assert.Equal("2.0", (string?)response["jsonrpc"]);
        Assert.Equal(7, (int?)response["id"]);
        Assert.Equal("ping", (string?)response["result"]!["method"]);
        Assert.Equal(1, (int?)response["result"]!["params"]!["x"]);
        Assert.Null(response["error"]);
    }

    [Fact]
    public async Task Serves_multiple_requests_per_connection_in_order()
    {
        using var server = Echo();
        int port = server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        for (int i = 1; i <= 3; i++)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(
                $$"""{"jsonrpc":"2.0","id":{{i}},"method":"m{{i}}"}""" + "\n"));
            var response = (JsonObject)JsonNode.Parse((await reader.ReadLineAsync())!)!;
            Assert.Equal(i, (int?)response["id"]);
            Assert.Equal($"m{i}", (string?)response["result"]!["method"]);
        }
    }

    [Fact]
    public async Task Serves_concurrent_connections()
    {
        using var server = Echo();
        int port = server.Start();

        var results = await Task.WhenAll(
            Roundtrip(port, """{"jsonrpc":"2.0","id":1,"method":"a"}"""),
            Roundtrip(port, """{"jsonrpc":"2.0","id":2,"method":"b"}"""));
        Assert.Equal("a", (string?)results.Single(r => (int?)r["id"] == 1)["result"]!["method"]);
        Assert.Equal("b", (string?)results.Single(r => (int?)r["id"] == 2)["result"]!["method"]);
    }

    [Fact]
    public async Task Token_gate_rejects_missing_and_wrong_tokens_before_dispatch()
    {
        bool dispatched = false;
        using var server = new RemoteControlServer(
            (_, _, _) => { dispatched = true; return Task.FromResult<JsonNode?>(null); },
            port: 0, token: "secret");
        int port = server.Start();

        var missing = await Roundtrip(port, """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        Assert.Equal(-32001, (int?)missing["error"]!["code"]);
        var wrong = await Roundtrip(port, """{"jsonrpc":"2.0","id":2,"method":"ping","token":"nope"}""");
        Assert.Equal(-32001, (int?)wrong["error"]!["code"]);
        Assert.False(dispatched, "an unauthorized request must never reach the handler");

        var right = await Roundtrip(port, """{"jsonrpc":"2.0","id":3,"method":"ping","token":"secret"}""");
        Assert.Null(right["error"]);
        Assert.True(dispatched);
    }

    [Fact]
    public async Task Failure_classes_map_to_their_jsonrpc_codes()
    {
        using var server = new RemoteControlServer(
            (method, _, _) => method switch
            {
                "boom" => throw new InvalidOperationException("handler blew up"),
                "coded" => throw new RemoteMethodException(-32601, "unknown method 'coded'"),
                _ => Task.FromResult<JsonNode?>(null),
            },
            port: 0);
        int port = server.Start();

        var parse = await Roundtrip(port, "this is not json");
        Assert.Equal(-32700, (int?)parse["error"]!["code"]);

        var noMethod = await Roundtrip(port, """{"jsonrpc":"2.0","id":1}""");
        Assert.Equal(-32600, (int?)noMethod["error"]!["code"]);

        var coded = await Roundtrip(port, """{"jsonrpc":"2.0","id":2,"method":"coded"}""");
        Assert.Equal(-32601, (int?)coded["error"]!["code"]);
        Assert.Contains("unknown method", (string?)coded["error"]!["message"]);

        var thrown = await Roundtrip(port, """{"jsonrpc":"2.0","id":3,"method":"boom"}""");
        Assert.Equal(-32000, (int?)thrown["error"]!["code"]);
        Assert.Contains("handler blew up", (string?)thrown["error"]!["message"]);
    }
}

/// <summary>A recording stub viewer: every call appends to <see cref="Calls"/> and
/// answers canned values — the seam that makes the RPC vocabulary testable without a
/// windowing system.</summary>
internal sealed class StubViewer : IRemoteViewer
{
    public List<string> Calls { get; } = [];
    public string? Selection = "Model/pin";

    public Task<IReadOnlyList<string>> ListPartsAsync()
    {
        Calls.Add("list");
        return Task.FromResult<IReadOnlyList<string>>(["Model/bracket", "Model/pin"]);
    }

    public Task SetViewAsync(string view) { Calls.Add($"view:{view}"); return Task.CompletedTask; }

    public Task FitAsync() { Calls.Add("fit"); return Task.CompletedTask; }

    public Task SetSectionAsync(bool enabled, SectionAxis axis, double? offset)
    {
        Calls.Add($"section:{enabled}:{axis}:{offset?.ToString() ?? "null"}");
        return Task.CompletedTask;
    }

    public Task SetViewStyleAsync(ViewStyle style) { Calls.Add($"style:{style}"); return Task.CompletedTask; }

    public Task<bool> SetDisplayModeAsync(string path, DisplayMode mode)
    {
        Calls.Add($"mode:{path}:{mode}");
        return Task.FromResult(path == "Model/pin");
    }

    public Task<bool> SelectAsync(string? path)
    {
        Calls.Add($"select:{path ?? "null"}");
        Selection = path;
        return Task.FromResult(path is null || path == "Model/pin");
    }

    public Task<string?> GetSelectionAsync() => Task.FromResult(Selection);

    public Task<(Vector3d A, Vector3d B, double Distance)?> MeasureAsync(
        double x1, double y1, double x2, double y2)
    {
        Calls.Add($"measure:{x1},{y1},{x2},{y2}");
        return Task.FromResult<(Vector3d, Vector3d, double)?>(
            x1 < 0 ? null : (new Vector3d(0, 0, 0), new Vector3d(3, 4, 0), 5.0));
    }

    public Task<string> ScreenshotAsync(string? path)
    {
        Calls.Add($"screenshot:{path ?? "default"}");
        return Task.FromResult(path ?? "C:/pictures/default.png");
    }
}

/// <summary>The method vocabulary over a stub viewer — what each JSON-RPC method
/// does, what it validates, and what it answers.</summary>
public class RemoteViewerDispatcherTests
{
    private static async Task<JsonNode?> Call(
        StubViewer viewer, string method, JsonObject? parameters = null) =>
        await RemoteViewerDispatcher.For(viewer, "test title")(method, parameters, CancellationToken.None);

    [Fact]
    public async Task Ping_reports_the_title_and_the_vocabulary()
    {
        var result = await Call(new StubViewer(), "ping");
        Assert.Equal("test title", (string?)result!["title"]);
        Assert.Contains("measure", result["methods"]!.AsArray().Select(m => (string?)m));
    }

    [Fact]
    public async Task SetView_validates_the_name_against_the_shared_table()
    {
        var viewer = new StubViewer();
        await Call(viewer, "set_view", new JsonObject { ["view"] = "front" });
        Assert.Contains("view:front", viewer.Calls);

        var error = await Assert.ThrowsAsync<RemoteMethodException>(
            () => Call(viewer, "set_view", new JsonObject { ["view"] = "sideways" }));
        Assert.Contains("iso, front", error.Message);
    }

    [Fact]
    public async Task Section_style_mode_and_selection_flow_through()
    {
        var viewer = new StubViewer();
        await Call(viewer, "set_section", new JsonObject { ["axis"] = "x", ["offset"] = 2.5 });
        await Call(viewer, "set_view_style", new JsonObject { ["style"] = "wireframe" });
        await Call(viewer, "set_display_mode",
            new JsonObject { ["part"] = "Model/pin", ["mode"] = "translucent" });
        await Call(viewer, "select_part", new JsonObject { ["part"] = "Model/pin" });
        await Call(viewer, "fit");

        Assert.Equal(
            ["section:True:X:2.5", "style:Wireframe", "mode:Model/pin:Translucent",
             "select:Model/pin", "fit"],
            viewer.Calls);

        var selection = await Call(viewer, "get_selection");
        Assert.Equal("Model/pin", (string?)selection!["selected"]);
    }

    [Fact]
    public async Task Unknown_parts_fail_naming_what_is_displayed()
    {
        var error = await Assert.ThrowsAsync<RemoteMethodException>(
            () => Call(new StubViewer(), "select_part", new JsonObject { ["part"] = "nope" }));
        Assert.Contains("Model/bracket", error.Message);
        Assert.Contains("Model/pin", error.Message);
    }

    [Fact]
    public async Task Measure_answers_points_and_distance_or_reports_a_miss()
    {
        var viewer = new StubViewer();
        var hit = await Call(viewer, "measure",
            new JsonObject { ["x1"] = 10, ["y1"] = 20, ["x2"] = 30, ["y2"] = 40 });
        Assert.True((bool?)hit!["hit"]);
        Assert.Equal(5.0, (double?)hit["distance"]);
        Assert.Equal(3.0, (double?)hit["b"]![0]);

        var miss = await Call(viewer, "measure",
            new JsonObject { ["x1"] = -1, ["y1"] = 0, ["x2"] = 0, ["y2"] = 0 });
        Assert.False((bool?)miss!["hit"]);
    }

    [Fact]
    public async Task Unknown_methods_and_missing_parameters_are_coded_errors()
    {
        var unknown = await Assert.ThrowsAsync<RemoteMethodException>(
            () => Call(new StubViewer(), "explode"));
        Assert.Equal(-32601, unknown.Code);
        Assert.Contains("set_view", unknown.Message);   // the vocabulary is named

        var missing = await Assert.ThrowsAsync<RemoteMethodException>(
            () => Call(new StubViewer(), "set_view"));
        Assert.Equal(-32602, missing.Code);
        Assert.Contains("'view'", missing.Message);
    }
}
