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
        return Task.FromResult<IReadOnlyList<string>>(Parts);
    }

    /// <summary>What list_parts answers; empty models the startup gap when
    /// <see cref="Ready"/> is also false.</summary>
    public IReadOnlyList<string> Parts = ["Model/bracket", "Model/pin"];

    /// <summary>False models the measured startup race: the port announced, the first
    /// instance list not yet adopted by the render pass.</summary>
    public bool Ready = true;

    public Task<bool> IsReadyAsync() => Task.FromResult(Ready);

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
        return Task.FromResult(path is null || (path == "Model/pin" && Parts.Contains(path)));
    }

    public Task<string?> GetSelectionAsync() => Task.FromResult(Selection);

    public Task<(Vector3d A, Vector3d B, double Distance)?> MeasureAsync(
        double x1, double y1, double x2, double y2)
    {
        Calls.Add($"measure:{x1},{y1},{x2},{y2}");
        return Task.FromResult<(Vector3d, Vector3d, double)?>(
            x1 < 0 ? null : (new Vector3d(0, 0, 0), new Vector3d(3, 4, 0), 5.0));
    }

    /// <summary>False models a window with no animation (what most model programs are —
    /// the host has to call WithAnimation), which the dispatcher must refuse BY NAME
    /// rather than answering "ok" and letting a still be captured as an instant.</summary>
    public bool Animated = true;

    public Task<double?> SetAnimationTimeAsync(double t)
    {
        Calls.Add($"seek:{t.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return Task.FromResult(Animated ? Math.Clamp(t, 0, 1) : (double?)null);
    }

    /// <summary>What a real viewer answers once the PNG is on disk — or, when
    /// <see cref="ScreenshotFailure"/> is set, the refusal a window that never rendered
    /// produces.</summary>
    public RemoteMethodException? ScreenshotFailure;

    public Task<string> ScreenshotAsync(string? path)
    {
        Calls.Add($"screenshot:{path ?? "default"}");
        return ScreenshotFailure is { } failure
            ? Task.FromException<string>(failure)
            : Task.FromResult(path ?? "C:/pictures/default.png");
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
    public async Task Ping_reports_readiness_so_a_client_can_poll_the_startup_gap()
    {
        // The measured race: the RPC port is announced from OnViewportReady while the
        // first instance list is still waiting for the render pass — so ping carries
        // "ready" and a client polls it instead of reading [] as "no parts".
        var viewer = new StubViewer { Ready = false };
        Assert.False((bool?)(await Call(viewer, "ping"))!["ready"]);

        viewer.Ready = true;
        Assert.True((bool?)(await Call(viewer, "ping"))!["ready"]);
    }

    [Fact]
    public async Task UnknownPart_before_readiness_says_not_yet_instead_of_no_parts()
    {
        // An empty part list has two causes with opposite fixes; the refusal must not
        // read as "this model has no parts" during the startup gap.
        var viewer = new StubViewer { Ready = false, Parts = [] };
        var raced = await Assert.ThrowsAsync<RemoteMethodException>(() =>
            Call(viewer, "select_part", new JsonObject { ["part"] = "Model/pin" }));
        Assert.Contains("not displayed its parts yet", raced.Message);
        Assert.Contains("ping", raced.Message);

        // A ready viewer with a genuinely empty model keeps the incumbent message.
        var empty = new StubViewer { Ready = true, Parts = [] };
        var refused = await Assert.ThrowsAsync<RemoteMethodException>(() =>
            Call(empty, "select_part", new JsonObject { ["part"] = "Model/pin" }));
        Assert.Contains("displayed parts:", refused.Message);
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
    public async Task Screenshot_answers_a_path_and_says_the_file_is_written()
    {
        // The "written" flag is not decoration: the method used to return as soon as the
        // capture was ARMED, so a client had a path and no way to know whether the bytes
        // existed. The contract is now that the answer arrives after the write.
        var viewer = new StubViewer();
        var result = await Call(viewer, "screenshot", new JsonObject { ["path"] = "C:/tmp/f.png" });

        Assert.Equal("C:/tmp/f.png", (string?)result!["path"]);
        Assert.True((bool?)result["written"]);
        Assert.Contains("screenshot:C:/tmp/f.png", viewer.Calls);
    }

    [Fact]
    public async Task A_window_that_never_renders_refuses_by_name_rather_than_promising_a_path()
    {
        var viewer = new StubViewer
        {
            ScreenshotFailure = new RemoteMethodException(-32000,
                "the viewer produced no frame within 10s, so 'x.png' was not written"),
        };

        var error = await Assert.ThrowsAsync<RemoteMethodException>(
            () => Call(viewer, "screenshot", new JsonObject { ["path"] = "x.png" }));
        Assert.Contains("no frame", error.Message);
    }

    [Fact]
    public async Task SetAnimationTime_parks_the_transport_and_says_it_stopped_the_clock()
    {
        // The headless screenshot tool takes a t and re-evaluates Animation.At(t); a live
        // window has its own playback position, so the only way to capture an instant is
        // to drive the transport there AND stop it. "playing" states that rather than
        // leaving a caller to assume it.
        var viewer = new StubViewer();
        var result = await Call(viewer, "set_animation_time", new JsonObject { ["t"] = 0.25 });

        Assert.Equal(0.25, (double?)result!["t"]);
        Assert.False((bool?)result["playing"]);
        Assert.Contains("seek:0.25", viewer.Calls);

        // Clamped, not refused: a timeline position is a fraction, and 1.4 plainly means
        // "the end" — the same rule AnimationPlayback.Seek applies to the scrubber.
        var clamped = await Call(viewer, "set_animation_time", new JsonObject { ["t"] = 1.4 });
        Assert.Equal(1.0, (double?)clamped!["t"]);
    }

    [Fact]
    public async Task A_viewer_with_no_animation_refuses_a_seek_by_name()
    {
        var viewer = new StubViewer { Animated = false };

        var error = await Assert.ThrowsAsync<RemoteMethodException>(
            () => Call(viewer, "set_animation_time", new JsonObject { ["t"] = 0.5 }));

        Assert.Equal(-32000, error.Code);
        Assert.Contains("no animation", error.Message);
        Assert.Contains("WithAnimation", error.Message);     // names the fix
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
