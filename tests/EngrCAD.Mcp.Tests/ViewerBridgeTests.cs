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

        public Task<string> ScreenshotAsync(string? path) =>
            Task.FromResult(path ?? "default.png");
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

            var screenshot = tools.Screenshot("C:/tmp/frame.png");
            Assert.False(screenshot.IsError == true);
            Assert.Contains("frame.png", Text(screenshot));
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
                             "viewer_screenshot"];
        foreach (string name in expected)
            Assert.Contains(bridged.ToolCollection!, t => t.ProtocolTool.Name == name);
        // And the headless surface is intact beside them.
        Assert.Contains(bridged.ToolCollection!, t => t.ProtocolTool.Name == "screenshot");
    }
}
