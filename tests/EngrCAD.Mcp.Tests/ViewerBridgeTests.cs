using System.Text.Json.Nodes;
using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using ModelContextProtocol.Protocol;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The live-viewer bridge, end to end and headless: real <see cref="ViewerTools"/> →
/// real <see cref="ViewerRpcClient"/> → real loopback <see cref="RemoteControlServer"/>
/// → real <see cref="RemoteViewerDispatcher"/> → a stub <see cref="IRemoteViewer"/>.
/// Everything except the window itself, which is exactly the layer the stub replaces.
/// </summary>
public class ViewerBridgeTests
{
    private sealed class StubViewer : IRemoteViewer
    {
        public List<string> Calls { get; } = [];
        public string? Selection;

        public Task<IReadOnlyList<string>> ListPartsAsync() =>
            Task.FromResult<IReadOnlyList<string>>(["Model/bracket", "Model/pin"]);

        public Task SetViewAsync(string view) { Calls.Add($"view:{view}"); return Task.CompletedTask; }

        public Task FitAsync() { Calls.Add("fit"); return Task.CompletedTask; }

        public Task SetSectionAsync(bool enabled, SectionAxis axis, double? offset)
        {
            Calls.Add($"section:{enabled}:{axis}:{offset?.ToString() ?? "null"}");
            return Task.CompletedTask;
        }

        public Task SetViewStyleAsync(ViewStyle style)
        {
            Calls.Add($"style:{style}");
            return Task.CompletedTask;
        }

        public Task<bool> SetDisplayModeAsync(string path, DisplayMode mode)
        {
            Calls.Add($"mode:{path}:{mode}");
            return Task.FromResult(path == "Model/pin");
        }

        public Task<bool> SelectAsync(string? path)
        {
            Selection = path;
            return Task.FromResult(path is null || path == "Model/pin");
        }

        public Task<string?> GetSelectionAsync() => Task.FromResult(Selection);

        public Task<(Vector3d A, Vector3d B, double Distance)?> MeasureAsync(
            double x1, double y1, double x2, double y2) =>
            Task.FromResult<(Vector3d, Vector3d, double)?>(
                (new Vector3d(0, 0, 0), new Vector3d(0, 0, 7), 7.0));

        /// <summary>Null models a window with no animation, the default for a model
        /// program that never called <c>WithAnimation</c>.</summary>
        public bool Animated = true;

        public Task<double?> SetAnimationTimeAsync(double t)
        {
            Calls.Add($"seek:{t.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return Task.FromResult(Animated ? Math.Clamp(t, 0, 1) : (double?)null);
        }

        /// <summary>A real viewer answers only once the PNG is on disk, so the stub
        /// writes one — that file IS what the bridge reads back and turns into an image
        /// block, and a stub that merely named a path would leave the read untested.</summary>
        public byte[]? Png;

        /// <summary>The refusal a window that never rendered produces.</summary>
        public RemoteMethodException? Failure;

        public Task<string> ScreenshotAsync(string? path)
        {
            if (Failure is { } failure)
                return Task.FromException<string>(failure);
            string target = path ?? Path.Combine(Path.GetTempPath(), "engrcad-stub.png");
            if (Png is { } png)
                File.WriteAllBytes(target, png);
            return Task.FromResult(target);
        }
    }

    private static (RemoteControlServer Server, StubViewer Viewer, ViewerTools Tools) Stack(
        string? serverToken = null, string? clientToken = null)
    {
        var viewer = new StubViewer();
        var server = new RemoteControlServer(
            RemoteViewerDispatcher.For(viewer, "bridge test"), port: 0, serverToken);
        int port = server.Start();
        return (server, viewer, new ViewerTools(new ViewerRpcClient(port, clientToken)));
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public void Bridge_tools_drive_the_viewer_through_the_socket()
    {
        var (server, viewer, tools) = Stack();
        using (server)
        {
            Assert.False(tools.SetView("front").IsError == true);
            Assert.False(tools.Fit().IsError == true);
            Assert.False(tools.SetSection(enabled: true, axis: "x", offset: 3).IsError == true);
            Assert.False(tools.SetViewStyle("wireframe").IsError == true);
            Assert.False(tools.SetDisplayMode("Model/pin", "translucent").IsError == true);

            Assert.Equal(
                ["view:front", "fit", "section:True:X:3", "style:Wireframe", "mode:Model/pin:Translucent"],
                viewer.Calls);
        }
    }

    [Fact]
    public void Selection_round_trips_and_measure_reports_the_distance()
    {
        var (server, _, tools) = Stack();
        using (server)
        {
            Assert.False(tools.SelectPart("Model/pin").IsError == true);
            var selection = tools.GetSelection();
            Assert.Contains("Model/pin", Text(selection));
            Assert.NotNull(selection.StructuredContent);

            var measured = tools.Measure(10, 10, 200, 200);
            Assert.False(measured.IsError == true);
            Assert.Contains("7", Text(measured));
        }
    }

    [Fact]
    public void Viewer_screenshot_returns_the_PNG_as_an_image_block_not_a_path()
    {
        // The tool used to answer "the viewer WILL write its next frame to <path>" — a
        // promise, since the window's capture happens on its next frame and the RPC
        // handler had no edge to wait on. The endpoint now completes after the write, so
        // the bridge simply reads the bytes back (legitimately: the endpoint is
        // loopback-only, so the file the viewer wrote is a file this process can open).
        var (server, viewer, tools) = Stack();
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-bridge-{Guid.NewGuid():N}.png");
        var bytes = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4 };
        viewer.Png = bytes;
        using (server)
        {
            var result = tools.Screenshot(path);
            Assert.False(result.IsError == true);

            var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.MimeType);
            // DecodedData, not Data: Data is the base64 the protocol carries.
            Assert.Equal(bytes, image.DecodedData.ToArray());
            // The path still travels, because a human may want to go and look at it.
            Assert.Contains(path, Text(result));
        }
        File.Delete(path);
    }

    [Fact]
    public void A_viewer_that_never_rendered_is_a_tool_error_carrying_its_own_message()
    {
        var (server, viewer, tools) = Stack();
        viewer.Failure = new RemoteMethodException(-32000,
            "the viewer produced no frame within 10s, so 'frame.png' was not written — a "
            + "minimised or occluded window may not render until it is shown");
        using (server)
        {
            var result = tools.Screenshot("frame.png");
            Assert.True(result.IsError == true);
            // The endpoint's own words, which name the real cause; a generic "capture
            // failed" would send a reader looking at the wrong thing.
            Assert.Contains("no frame", Text(result));
            Assert.Contains("occluded", Text(result));
        }
    }

    [Fact]
    public void A_capture_the_bridge_cannot_read_back_names_the_file_it_could_not_open()
    {
        // The viewer reported success but the bytes are not reachable from here. The
        // capture itself is fine, so the path is worth saying out loud rather than
        // swallowing into "screenshot failed".
        var (server, viewer, tools) = Stack();
        viewer.Png = null;   // answers a path, writes nothing
        string missing = Path.Combine(Path.GetTempPath(), $"engrcad-absent-{Guid.NewGuid():N}.png");
        using (server)
        {
            var result = tools.Screenshot(missing);
            Assert.True(result.IsError == true);
            Assert.Contains(missing, Text(result));
        }
    }

    [Fact]
    public void Set_animation_time_parks_the_transport_and_refuses_a_window_without_one()
    {
        // The parity gap this closes: the headless `screenshot` tool takes a t and
        // re-evaluates the animation for that instant, while a live window has its own
        // playback position — so the bridge drives the transport, then captures.
        var (server, viewer, tools) = Stack();
        using (server)
        {
            var parked = tools.SetAnimationTime(0.75);
            Assert.False(parked.IsError == true);
            Assert.Contains("0.75", Text(parked));
            Assert.Contains("paused", Text(parked));
            Assert.Contains("seek:0.75", viewer.Calls);

            viewer.Animated = false;
            var refused = tools.SetAnimationTime(0.5);
            Assert.True(refused.IsError == true);
            Assert.Contains("no animation", Text(refused));
        }
    }

    [Fact]
    public void Endpoint_errors_come_back_as_tool_errors_naming_what_exists()
    {
        var (server, _, tools) = Stack();
        using (server)
        {
            var unknownView = tools.SetView("sideways");
            Assert.True(unknownView.IsError == true);
            Assert.Contains("iso, front", Text(unknownView));

            var unknownPart = tools.SetDisplayMode("nope", "shaded");
            Assert.True(unknownPart.IsError == true);
            Assert.Contains("Model/bracket", Text(unknownPart));
        }
    }

    [Fact]
    public void A_wrong_token_is_a_tool_error_not_a_hang_or_a_crash()
    {
        var (server, viewer, tools) = Stack(serverToken: "secret", clientToken: "wrong");
        using (server)
        {
            var result = tools.Fit();
            Assert.True(result.IsError == true);
            Assert.Contains("unauthorized", Text(result));
            Assert.Empty(viewer.Calls);
        }

        var (server2, viewer2, tools2) = Stack(serverToken: "secret", clientToken: "secret");
        using (server2)
        {
            Assert.False(tools2.Fit().IsError == true);
            Assert.Equal(["fit"], viewer2.Calls);
        }
    }

    [Fact]
    public void No_viewer_listening_is_a_tool_error_naming_the_rpc_flag()
    {
        // Bind-then-close to get a port that is certainly not listening.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int deadPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var tools = new ViewerTools(new ViewerRpcClient(deadPort));
        var result = tools.Fit();
        Assert.True(result.IsError == true);
        Assert.Contains("No live viewer", Text(result));
        Assert.Contains("--rpc", Text(result));
    }

    [Fact]
    public void Bridge_tools_are_served_only_when_a_viewer_endpoint_is_configured()
    {
        var sceneTools = new SceneTools(new SceneSession(TestScenes.Basic()));

        var plain = EngrCadMcpServer.BuildOptions(sceneTools, "t");
        Assert.DoesNotContain(plain.ToolCollection!, t => t.ProtocolTool.Name == "set_view");

        var bridged = EngrCadMcpServer.BuildOptions(
            sceneTools, "t", new ViewerTools(new ViewerRpcClient(1)));
        string[] expected = ["set_view", "fit", "set_section", "set_view_style",
                             "set_display_mode", "select_part", "get_selection", "measure",
                             "set_animation_time", "viewer_screenshot"];
        foreach (string name in expected)
            Assert.Contains(bridged.ToolCollection!, t => t.ProtocolTool.Name == name);
        // And the headless surface is intact beside them.
        Assert.Contains(bridged.ToolCollection!, t => t.ProtocolTool.Name == "screenshot");
    }
}
