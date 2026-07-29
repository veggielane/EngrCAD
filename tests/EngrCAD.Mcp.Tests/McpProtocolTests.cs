using System.IO.Pipelines;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// A real MCP session — initialize, tools/list, tools/call, resources — over an
/// in-process pipe pair instead of a child process. This is what proves the SDK wiring
/// (schemas, result shapes, the image content block) rather than just the handlers.
/// Shares the "offscreen-gl" collection because one test renders.
/// </summary>
[Collection("offscreen-gl")]
public class McpProtocolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>Runs <paramref name="body"/> against a live client connected to a
    /// server serving the standard test scene (or <paramref name="scene"/>).</summary>
    private static async Task WithSession(Func<McpClient, Task> body, Modeling.Scene? scene = null)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var toServer = new Pipe();
        var toClient = new Pipe();
        var tools = new SceneTools(new SceneSession(scene ?? TestScenes.Basic()));

        var server = EngrCadMcpServer.RunAsync(
            toServer.Reader.AsStream(), toClient.Writer.AsStream(), tools, "test model", cts.Token);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: cts.Token);

        try
        {
            await body(client);
        }
        finally
        {
            await cts.CancelAsync();
            try { await server; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Initialize_reports_the_server_identity_and_instructions()
    {
        await WithSession(client =>
        {
            Assert.Equal("engrcad", client.ServerInfo.Name);
            Assert.Equal("test model", client.ServerInfo.Title);
            Assert.NotNull(client.ServerCapabilities.Tools);
            Assert.NotNull(client.ServerCapabilities.Resources);
            Assert.Contains("list_parts", client.ServerInstructions);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ToolsList_advertises_the_v1_surface_with_schemas()
    {
        await WithSession(async client =>
        {
            var tools = await client.ListToolsAsync();
            Assert.Equal(
                ["describe_part", "export", "list_parts", "list_tabs", "load_document", "reload",
                 "save_document", "screenshot", "set_param", "suppress_feature", "unsuppress_feature"],
                tools.Select(t => t.Name).Order());

            var screenshot = tools.Single(t => t.Name == "screenshot");
            var properties = screenshot.ProtocolTool.InputSchema.GetProperty("properties");
            foreach (string expected in (string[])["view", "style", "tab", "part", "sectionAxis",
                                                   "sectionOffset", "width", "height", "ambientOcclusion",
                                                   "t"])
            {
                Assert.True(properties.TryGetProperty(expected, out _), $"missing '{expected}' in the schema");
            }
            // Every parameter is optional: a bare screenshot call must be legal.
            Assert.False(screenshot.ProtocolTool.InputSchema.TryGetProperty("required", out var required)
                         && required.GetArrayLength() > 0);

            // describe_part's one required parameter is the part name.
            var describe = tools.Single(t => t.Name == "describe_part");
            Assert.Equal("name",
                describe.ProtocolTool.InputSchema.GetProperty("required")[0].GetString());
        });
    }

    [Fact]
    public async Task CallTool_returns_the_tool_layer_json()
    {
        await WithSession(async client =>
        {
            var result = await client.CallToolAsync("list_parts");
            var payload = Assert.IsType<JsonObject>(JsonNode.Parse(TextOf(result)));
            Assert.Equal(3, Assert.IsType<JsonArray>(payload["parts"]).Count);

            var described = await client.CallToolAsync(
                "describe_part", new Dictionary<string, object?> { ["name"] = "pin" });
            Assert.Contains("\"closed\": true", TextOf(described));
        });
    }

    [Fact]
    public async Task JsonTools_declare_output_schemas_and_return_structured_content()
    {
        await WithSession(async client =>
        {
            var tools = await client.ListToolsAsync();

            // Every JSON-returning tool advertises an output schema; screenshot is an
            // image and deliberately does not.
            string[] structured = ["list_tabs", "list_parts", "describe_part", "export",
                                   "reload", "set_param", "suppress_feature", "unsuppress_feature"];
            foreach (string name in structured)
            {
                var tool = tools.Single(t => t.Name == name);
                Assert.True(tool.ProtocolTool.OutputSchema is not null, $"'{name}' has no output schema");
                Assert.Equal("object",
                    tool.ProtocolTool.OutputSchema!.Value.GetProperty("type").GetString());
            }
            Assert.Null(tools.Single(t => t.Name == "screenshot").ProtocolTool.OutputSchema);

            // structuredContent and the text block are the same payload, byte for byte
            // of JSON meaning: one JsonObject feeds both.
            var result = await client.CallToolAsync("list_parts");
            Assert.NotNull(result.StructuredContent);
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(TextOf(result)),
                JsonNode.Parse(result.StructuredContent!.Value.GetRawText())));

            var parts = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!["parts"]!.AsArray();
            Assert.Equal(3, parts.Count);
        });
    }

    [Fact]
    public async Task SetParam_over_the_protocol_returns_the_structured_regeneration_report()
    {
        await WithSession(async client =>
        {
            var result = await client.CallToolAsync("set_param", new Dictionary<string, object?>
            {
                ["part"] = "plate",
                ["feature"] = "Base",
                ["param"] = "Height",
                ["value"] = 9,
            });

            Assert.False(result.IsError == true, TextOf(result));
            Assert.NotNull(result.StructuredContent);
            var report = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
            Assert.True((bool?)report["succeeded"]);
            Assert.True((bool?)report["geometryUpdated"]);
            Assert.Equal(9, (double?)report["value"]);
            Assert.All(report["features"]!.AsArray(),
                f => Assert.Equal("applied", (string?)f!["outcome"]));
        }, TestScenes.Parametric());
    }

    [Fact]
    public async Task CallTool_reports_a_bad_argument_as_a_tool_error_not_a_protocol_error()
    {
        await WithSession(async client =>
        {
            var result = await client.CallToolAsync(
                "describe_part", new Dictionary<string, object?> { ["name"] = "nope" });
            Assert.True(result.IsError);
            Assert.Contains("No part named 'nope'", TextOf(result));
        });
    }

    [Fact]
    public async Task Resources_serve_the_scene_summary()
    {
        await WithSession(async client =>
        {
            var resources = await client.ListResourcesAsync();
            Assert.Equal("engrcad://scene", Assert.Single(resources).Uri);

            var read = await client.ReadResourceAsync("engrcad://scene");
            var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
            var json = Assert.IsType<JsonObject>(JsonNode.Parse(text.Text));
            Assert.Equal(2, Assert.IsType<JsonArray>(json["tabs"]).Count);
        });
    }

    [SkippableFact]
    public async Task Screenshot_comes_back_as_an_image_content_block()
    {
        Skip.IfNot(Viewer.EngrCad.CanRenderToImage,
            $"offscreen GL unavailable: {Viewer.OffscreenRenderer.UnavailableReason}");

        await WithSession(async client =>
        {
            var result = await client.CallToolAsync("screenshot", new Dictionary<string, object?>
            {
                ["view"] = "front",
                ["width"] = 320,
                ["height"] = 240,
            });

            Assert.False(result.IsError == true, TextOf(result));
            var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.MimeType);

            var png = image.DecodedData.Span;
            Assert.True(png.Length > 1000, $"a 320x240 render should not be {png.Length} bytes");
            Assert.Equal<byte[]>([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4].ToArray());
            // IHDR width/height, big-endian at offsets 16 and 20.
            Assert.Equal(320, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
            Assert.Equal(240, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);

            Assert.Contains("front view", TextOf(result));
        });
    }
}
