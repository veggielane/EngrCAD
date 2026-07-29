using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// Remote control of a RUNNING viewer window (todo.md's option (b)): a loopback TCP
// endpoint carrying newline-delimited JSON-RPC 2.0, exposed by the viewer itself
// behind an opt-in flag (EngrCadOptions.WithRemoteControl / --rpc), with the MCP
// server bridging to it from a separate process. Three layers, separable on purpose:
//
//   RemoteControlServer    — transport only: sockets, framing, token check, error
//                            envelopes. Takes a handler delegate; fully testable over
//                            a real socket with a stub handler, no window.
//   RemoteViewerDispatcher — the method vocabulary: JSON in/out over IRemoteViewer.
//                            Pure; testable with a stub IRemoteViewer.
//   ViewportRemoteViewer   — IRemoteViewer over a real ViewportControl. EVERY call
//                            marshals through Dispatcher.UIThread — the viewer's
//                            non-negotiable threading rule — and GL is never touched
//                            here (screenshots ride CaptureScreenshotAsync's
//                            capture-on-next-frame path). ARMING is UI-thread work;
//                            WAITING for the frame deliberately is not, since blocking
//                            the dispatcher is how the frame would fail to arrive.
//
// Security posture: loopback only (hardcoded — the listener binds IPAddress.Loopback
// and nothing else), OFF by default, optional shared token checked on every request.
// This endpoint can move the camera and write screenshot files, so it must never be
// on implicitly.

/// <summary>Opt-in remote control settings (see <see cref="EngrCadOptions.RemoteControl"/>).</summary>
public sealed class RemoteControlOptions
{
    /// <summary>TCP port on 127.0.0.1; 0 (the default) picks an ephemeral port,
    /// reported through the log and the status overlay.</summary>
    public int Port { get; set; }

    /// <summary>Optional shared secret; when set, every request must carry it as a
    /// top-level <c>"token"</c> member or it is rejected without dispatch.</summary>
    public string? Token { get; set; }
}

/// <summary>
/// A minimal newline-delimited JSON-RPC 2.0 server on the loopback interface.
/// Transport only: framing, the optional token check, and the error envelope live
/// here; what the methods DO is the handler's business. Multiple concurrent
/// connections are served; requests on one connection are answered in order.
/// </summary>
public sealed class RemoteControlServer : IDisposable
{
    /// <summary>Handles one request: method + params (may be null) → result node
    /// (null = JSON null result). Throw to produce a JSON-RPC error response.</summary>
    public delegate Task<JsonNode?> Handler(string method, JsonObject? parameters, CancellationToken cancellationToken);

    private readonly Handler _handler;
    private readonly string? _token;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();

    public RemoteControlServer(Handler handler, int port = 0, string? token = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _token = token;
        // Loopback by construction — there is deliberately no way to bind wider.
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>The bound port (valid after <see cref="Start"/>).</summary>
    public int Port { get; private set; }

    /// <summary>Binds the loopback listener and starts accepting; returns the actual
    /// port (useful with port 0).</summary>
    public int Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
        return Port;
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                _ = ServeClient(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task ServeClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                while (!_stopping.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(_stopping.Token).ConfigureAwait(false);
                    if (line is null)
                        return;                     // client closed
                    if (line.Length == 0)
                        continue;
                    string response = await Respond(line).ConfigureAwait(false);
                    await writer.WriteLineAsync(response.AsMemory(), _stopping.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }                 // client went away mid-frame
        }
    }

    private async Task<string> Respond(string line)
    {
        JsonNode? id = null;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject request)
                return ErrorResponse(null, -32700, "parse error: expected a JSON object per line");
            id = request["id"]?.DeepClone();
            if (request["method"] is not JsonValue methodValue
                || methodValue.GetValueKind() != JsonValueKind.String)
                return ErrorResponse(id, -32600, "invalid request: no method");
            // Constant-shape token check BEFORE any dispatch: a wrong or missing token
            // learns nothing about the vocabulary.
            if (_token is not null && (string?)request["token"] != _token)
                return ErrorResponse(id, -32001, "unauthorized: missing or wrong token");

            var result = await _handler(
                (string)methodValue!, request["params"] as JsonObject, _stopping.Token).ConfigureAwait(false);
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result,
            }.ToJsonString();
        }
        catch (JsonException e)
        {
            return ErrorResponse(id, -32700, $"parse error: {e.Message}");
        }
        catch (RemoteMethodException e)
        {
            return ErrorResponse(id, e.Code, e.Message);
        }
        catch (Exception e)
        {
            return ErrorResponse(id, -32000, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static string ErrorResponse(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    }.ToJsonString();

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _stopping.Dispose();
    }
}

/// <summary>A handler failure with a specific JSON-RPC error code (the dispatcher
/// throws it for unknown methods and bad arguments).</summary>
public sealed class RemoteMethodException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// What a remotely controllable viewer can do — the seam between the RPC vocabulary
/// and the window. <see cref="ViewportRemoteViewer"/> is the real one (UI-thread
/// marshaling inside); tests substitute a stub, which is what makes the whole RPC
/// stack testable without a window.
/// </summary>
public interface IRemoteViewer
{
    /// <summary>Displayed instances' occurrence paths, in instance order.</summary>
    Task<IReadOnlyList<string>> ListPartsAsync();

    /// <summary>Snap to a standard view (a <c>ViewCubeMath.StandardViewNames</c> name).</summary>
    Task SetViewAsync(string view);

    /// <summary>Zoom-to-fit the visible parts.</summary>
    Task FitAsync();

    /// <summary>Toggle/position the axis-aligned section cut.</summary>
    Task SetSectionAsync(bool enabled, SectionAxis axis, double? offset);

    /// <summary>Set the global view style.</summary>
    Task SetViewStyleAsync(ViewStyle style);

    /// <summary>Set one part's display mode; false when no instance has that path.</summary>
    Task<bool> SetDisplayModeAsync(string path, DisplayMode mode);

    /// <summary>Select by occurrence path (null clears); false when not found.</summary>
    Task<bool> SelectAsync(string? path);

    /// <summary>The selected instance's path, or null.</summary>
    Task<string?> GetSelectionAsync();

    /// <summary>Measure between two control-space points (DIPs); null when a pick misses.</summary>
    Task<(Vector3d A, Vector3d B, double Distance)?> MeasureAsync(double x1, double y1, double x2, double y2);

    /// <summary>Capture the next rendered frame to a PNG at <paramref name="path"/>
    /// (null = the default Pictures/EngrCAD path), completing with the path <b>once the
    /// file has been written</b> — so a caller may read the bytes back. Throws
    /// <see cref="RemoteMethodException"/> when no frame was produced in time.</summary>
    Task<string> ScreenshotAsync(string? path);
}

/// <summary>
/// The remote-control method vocabulary: maps JSON-RPC methods onto
/// <see cref="IRemoteViewer"/>. Pure translation — no sockets, no Avalonia — so the
/// vocabulary is locked by tests against a stub viewer.
/// </summary>
public static class RemoteViewerDispatcher
{
    /// <summary>The methods this endpoint serves (for error messages and docs).</summary>
    public static IReadOnlyList<string> Methods { get; } =
    [
        "ping", "list_parts", "set_view", "fit", "set_section", "set_display_mode",
        "set_view_style", "select_part", "get_selection", "measure", "screenshot",
    ];

    /// <summary>Wraps a viewer as a <see cref="RemoteControlServer.Handler"/>.</summary>
    public static RemoteControlServer.Handler For(IRemoteViewer viewer, string title = "EngrCAD") =>
        (method, parameters, _) => DispatchAsync(viewer, title, method, parameters);

    private static async Task<JsonNode?> DispatchAsync(
        IRemoteViewer viewer, string title, string method, JsonObject? p)
    {
        switch (method)
        {
            case "ping":
                return new JsonObject { ["title"] = title, ["methods"] = new JsonArray([.. Methods.Select(m => (JsonNode)m)]) };

            case "list_parts":
                return new JsonArray([.. (await viewer.ListPartsAsync().ConfigureAwait(false)).Select(p2 => (JsonNode)p2)]);

            case "set_view":
            {
                string view = RequireString(p, "view");
                if (ViewCubeMath.DirectionFor(view) is null)
                    throw new RemoteMethodException(-32602,
                        $"unknown view '{view}' — use one of: {string.Join(", ", ViewCubeMath.StandardViewNames)}");
                await viewer.SetViewAsync(view).ConfigureAwait(false);
                return new JsonObject { ["view"] = view };
            }

            case "fit":
                await viewer.FitAsync().ConfigureAwait(false);
                return new JsonObject { ["ok"] = true };

            case "set_section":
            {
                bool enabled = p?["enabled"]?.GetValue<bool>() ?? true;
                var axis = (p?["axis"] as JsonValue)?.GetValue<string>()?.ToLowerInvariant() switch
                {
                    null or "z" => SectionAxis.Z,
                    "x" => SectionAxis.X,
                    "y" => SectionAxis.Y,
                    var other => throw new RemoteMethodException(-32602, $"unknown axis '{other}' — use x, y, or z"),
                };
                double? offset = TryNumber(p, "offset");
                await viewer.SetSectionAsync(enabled, axis, offset).ConfigureAwait(false);
                return new JsonObject { ["enabled"] = enabled };
            }

            case "set_view_style":
            {
                string style = RequireString(p, "style").ToLowerInvariant();
                ViewStyle parsed = style switch
                {
                    "shaded-edges" or "shadedwithedges" => ViewStyle.ShadedWithEdges,
                    "shaded" => ViewStyle.Shaded,
                    "wireframe" => ViewStyle.Wireframe,
                    "points" => ViewStyle.Points,
                    _ => throw new RemoteMethodException(-32602,
                        $"unknown style '{style}' — use shaded-edges, shaded, wireframe, or points"),
                };
                await viewer.SetViewStyleAsync(parsed).ConfigureAwait(false);
                return new JsonObject { ["style"] = style };
            }

            case "set_display_mode":
            {
                string part = RequireString(p, "part");
                string mode = RequireString(p, "mode").ToLowerInvariant();
                DisplayMode parsed = mode switch
                {
                    "shaded" => DisplayMode.Shaded,
                    "wireframe" => DisplayMode.Wireframe,
                    "translucent" => DisplayMode.Translucent,
                    _ => throw new RemoteMethodException(-32602,
                        $"unknown mode '{mode}' — use shaded, wireframe, or translucent"),
                };
                if (!await viewer.SetDisplayModeAsync(part, parsed).ConfigureAwait(false))
                    throw await UnknownPart(viewer, part).ConfigureAwait(false);
                return new JsonObject { ["part"] = part, ["mode"] = mode };
            }

            case "select_part":
            {
                string? part = (p?["part"] as JsonValue)?.GetValue<string>();
                if (!await viewer.SelectAsync(part).ConfigureAwait(false))
                    throw await UnknownPart(viewer, part ?? "(null)").ConfigureAwait(false);
                return new JsonObject { ["selected"] = part };
            }

            case "get_selection":
                return new JsonObject { ["selected"] = await viewer.GetSelectionAsync().ConfigureAwait(false) };

            case "measure":
            {
                double x1 = RequireNumber(p, "x1"), y1 = RequireNumber(p, "y1");
                double x2 = RequireNumber(p, "x2"), y2 = RequireNumber(p, "y2");
                var measured = await viewer.MeasureAsync(x1, y1, x2, y2).ConfigureAwait(false);
                if (measured is not { } m)
                    return new JsonObject { ["hit"] = false };
                return new JsonObject
                {
                    ["hit"] = true,
                    ["a"] = new JsonArray(m.A.X, m.A.Y, m.A.Z),
                    ["b"] = new JsonArray(m.B.X, m.B.Y, m.B.Z),
                    ["distance"] = m.Distance,
                };
            }

            case "screenshot":
            {
                string? path = (p?["path"] as JsonValue)?.GetValue<string>();
                // Returns once the file EXISTS, so the bridge can read the bytes back and
                // answer with an image rather than a promise; "written" says so explicitly
                // rather than leaving a client to guess from a path it did not ask for.
                string target = await viewer.ScreenshotAsync(path).ConfigureAwait(false);
                return new JsonObject { ["path"] = target, ["written"] = true };
            }

            default:
                throw new RemoteMethodException(-32601,
                    $"unknown method '{method}' — this endpoint serves: {string.Join(", ", Methods)}");
        }
    }

    private static async Task<RemoteMethodException> UnknownPart(IRemoteViewer viewer, string part)
    {
        var paths = await viewer.ListPartsAsync().ConfigureAwait(false);
        return new RemoteMethodException(-32602,
            $"no part '{part}' — displayed parts: {string.Join(", ", paths)}");
    }

    private static string RequireString(JsonObject? p, string name) =>
        (p?[name] as JsonValue)?.GetValueKind() == JsonValueKind.String
            ? (string)p![name]!
            : throw new RemoteMethodException(-32602, $"missing required string parameter '{name}'");

    private static double RequireNumber(JsonObject? p, string name) =>
        TryNumber(p, name)
            ?? throw new RemoteMethodException(-32602, $"missing required number parameter '{name}'");

    /// <summary>Reads a numeric member whether the node wraps a parsed JSON number or
    /// an in-memory int/long/double (a <c>JsonValue&lt;int&gt;</c> refuses a plain
    /// double cast — the trap this helper exists for).</summary>
    private static double? TryNumber(JsonObject? p, string name)
    {
        if (p?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out double d))
            return d;
        // TryGetValue<T> matches the WRAPPED type exactly for in-memory nodes, so an
        // int-backed and a long-backed node each need their own ask.
        if (value.TryGetValue(out int i))
            return i;
        if (value.TryGetValue(out long l))
            return l;
        return null;
    }
}

/// <summary>
/// The real <see cref="IRemoteViewer"/>: every member marshals onto the Avalonia UI
/// thread (the viewer's rule — camera, selection, picking and display state are UI
/// state) and returns when the change has been applied there. GL is never touched:
/// screenshot rides <see cref="ViewportControl.SaveScreenshot"/>'s
/// capture-on-next-frame path, and geometry mutations go through the already
/// thread-safe seams. The invoker is injectable so the marshaling contract is
/// testable without a windowing system.
/// </summary>
public sealed class ViewportRemoteViewer(
    ViewportControl viewport, Func<Func<object?>, Task<object?>>? invoke = null) : IRemoteViewer
{
    private readonly Func<Func<object?>, Task<object?>> _invoke = invoke ??
        (work => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(work).GetTask());

    private async Task<T> OnUi<T>(Func<T> work) => (T)(await _invoke(() => work()).ConfigureAwait(false))!;

    private Task OnUi(Action work) => _invoke(() => { work(); return null; });

    public Task<IReadOnlyList<string>> ListPartsAsync() => OnUi(() => viewport.InstancePaths);

    public Task SetViewAsync(string view) => OnUi(() =>
    {
        var direction = ViewCubeMath.DirectionFor(view)
            ?? throw new RemoteMethodException(-32602, $"unknown view '{view}'");
        var camera = viewport.Camera;
        var (yaw, pitch) = ViewCubeMath.PoseFor(direction, camera.Yaw);
        // Same rule as the view cube and toolbar: pose changes, distance/target stay.
        viewport.Camera = camera with { Yaw = yaw, Pitch = pitch };
    });

    public Task FitAsync() => OnUi(viewport.Frame);

    public Task SetSectionAsync(bool enabled, SectionAxis axis, double? offset) => OnUi(() =>
    {
        viewport.SectionAxis = axis;
        if (offset is { } o)
            viewport.SectionOffset = o;
        viewport.SectionEnabled = enabled;
    });

    public Task SetViewStyleAsync(ViewStyle style) => OnUi(() => { viewport.ViewStyle = style; });

    public Task<bool> SetDisplayModeAsync(string path, DisplayMode mode) => OnUi(() =>
    {
        int index = IndexOf(path);
        if (index < 0)
            return false;
        viewport.SetDisplayMode(index, mode);
        return true;
    });

    public Task<bool> SelectAsync(string? path) => OnUi(() =>
    {
        if (path is null)
        {
            viewport.Select(-1);
            return true;
        }
        int index = IndexOf(path);
        if (index < 0)
            return false;
        viewport.Select(index);
        return true;
    });

    public Task<string?> GetSelectionAsync() => OnUi(() => viewport.SelectedPath);

    public Task<(Vector3d A, Vector3d B, double Distance)?> MeasureAsync(
        double x1, double y1, double x2, double y2) =>
        OnUi(() => viewport.Measure(new Avalonia.Point(x1, y1), new Avalonia.Point(x2, y2)));

    /// <summary>
    /// How long <see cref="ScreenshotAsync"/> waits for the window to produce a frame.
    /// <para>A deadline is not optional here: a minimised, occluded or otherwise idle
    /// window may not render for a very long time, and a hung RPC connection is a worse
    /// answer than an honest refusal. Kept comfortably under
    /// <c>ViewerRpcClient</c>'s own 15 s so the caller sees THIS message rather than a
    /// socket timeout with nothing in it.</para>
    /// </summary>
    public TimeSpan ScreenshotTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<string> ScreenshotAsync(string? path)
    {
        string target = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "EngrCAD", $"engrcad-rpc-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        // Arming is UI-thread work (it posts a render request); WAITING is not, and must
        // not be — blocking the dispatcher is exactly how the frame would fail to arrive.
        var capture = await OnUi(() => viewport.CaptureScreenshotAsync(target)).ConfigureAwait(false);
        try
        {
            // WaitAsync rather than a WhenAny race: it leaves no timer running behind a
            // successful capture, and a capture that FAILED (an unwritable path) surfaces
            // its own exception instead of being reported as a timeout it was not.
            return await capture.WaitAsync(ScreenshotTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RemoteMethodException(-32000,
                $"the viewer produced no frame within {ScreenshotTimeout.TotalSeconds:G3}s, so "
                + $"'{target}' was not written — a minimised or occluded window may not render "
                + "until it is shown");
        }
    }

    private int IndexOf(string path)
    {
        var paths = viewport.InstancePaths;
        for (int i = 0; i < paths.Count; i++)
        {
            if (string.Equals(paths[i], path, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
