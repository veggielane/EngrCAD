using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The real thing: a child process running a design program with <c>--mcp</c>, driven
/// with hand-written JSON-RPC frames over its stdin/stdout.
/// <para>Its reason for existing is <b>stdout purity</b>. The stdio transport IS
/// stdout, so one stray <c>Console.WriteLine</c> anywhere in the process breaks every
/// client. <c>EngrCAD.Mcp.TestModel</c> deliberately prints while it builds its scene;
/// these tests assert that line never reaches stdout, that every stdout line is a
/// JSON-RPC frame, and that the noise turns up on stderr where clients expect server
/// logging.</para>
/// </summary>
[Collection("offscreen-gl")]
public class StdioSessionTests
{
    private const string NoiseMarker = "must NOT reach stdout";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task Stdout_carries_protocol_frames_and_nothing_else()
    {
        var session = await RunSession(
            [
                Request(1, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "engrcad-test", ["version"] = "1.0" },
                }),
                Notification("notifications/initialized"),
                Request(2, "tools/list"),
                Request(3, "tools/call", new JsonObject
                {
                    ["name"] = "describe_part",
                    ["arguments"] = new JsonObject { ["name"] = "pin" },
                }),
            ],
            expectedResponses: 3);

        // 1. Every single line on stdout parses as a JSON-RPC frame. This is the assertion
        //    the whole feature hinges on.
        Assert.NotEmpty(session.StdoutLines);
        foreach (string line in session.StdoutLines)
        {
            JsonObject frame;
            try
            {
                frame = Assert.IsType<JsonObject>(JsonNode.Parse(line));
            }
            catch (JsonException e)
            {
                throw new Xunit.Sdk.XunitException(
                    $"stdout line is not JSON — the protocol stream is corrupted: '{line}' ({e.Message})");
            }
            Assert.Equal("2.0", (string?)frame["jsonrpc"]);
        }

        // 2. The model's own printing went to stderr, not into the stream.
        Assert.DoesNotContain(NoiseMarker, string.Join("\n", session.StdoutLines));
        Assert.Contains(NoiseMarker, session.Stderr);

        // 3. The session actually worked: initialize, tools/list, and a real tool call.
        var initialize = session.Response(1);
        Assert.Equal("engrcad", (string?)initialize["result"]!["serverInfo"]!["name"]);

        var tools = Assert.IsType<JsonArray>(session.Response(2)["result"]!["tools"]);
        Assert.Contains("screenshot", tools.Select(t => (string?)t!["name"]));

        string described = Text(session.Response(3));
        Assert.Contains("\"closed\": true", described);
        Assert.Contains("\"constructionTree\"", described);
    }

    [SkippableFact]
    public async Task Screenshot_over_stdio_returns_a_real_png_of_the_model()
    {
        Skip.IfNot(Viewer.EngrCad.CanRenderToImage,
            $"offscreen GL unavailable: {Viewer.OffscreenRenderer.UnavailableReason}");

        var session = await RunSession(
            [
                Request(1, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "engrcad-test", ["version"] = "1.0" },
                }),
                Notification("notifications/initialized"),
                Request(2, "tools/call", new JsonObject
                {
                    ["name"] = "screenshot",
                    ["arguments"] = new JsonObject { ["view"] = "iso", ["width"] = 480, ["height"] = 360 },
                }),
            ],
            expectedResponses: 2);

        var content = Assert.IsType<JsonArray>(session.Response(2)["result"]!["content"]);
        var image = content.OfType<JsonObject>().Single(b => (string?)b["type"] == "image");
        Assert.Equal("image/png", (string?)image["mimeType"]);

        byte[] png = Convert.FromBase64String((string)image["data"]!);
        Assert.Equal<byte[]>([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal(480, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(360, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        Assert.True(png.Length > 4000, $"a 480x360 render of three parts should not be {png.Length} bytes");

        // Still pure, even with 100 KB of base64 image flowing through the stream.
        foreach (string line in session.StdoutLines)
            Assert.Equal("2.0", (string?)Assert.IsType<JsonObject>(JsonNode.Parse(line))["jsonrpc"]);
    }

    // ---- driving the child process ----

    private sealed record Session(List<string> StdoutLines, string Stderr, int ExitCode)
    {
        public JsonObject Response(int id) =>
            StdoutLines
                .Select(l => JsonNode.Parse(l) as JsonObject)
                .OfType<JsonObject>()
                .Single(f => (int?)f["id"] == id);
    }

    private static string Text(JsonObject response) =>
        string.Concat(Assert.IsType<JsonArray>(response["result"]!["content"])
            .OfType<JsonObject>()
            .Where(b => (string?)b["type"] == "text")
            .Select(b => (string?)b["text"]));

    private static string Request(int id, string method, JsonObject? parameters = null)
    {
        var frame = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            frame["params"] = parameters;
        return frame.ToJsonString();
    }

    private static string Notification(string method) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString();

    private static async Task<Session> RunSession(string[] frames, int expectedResponses)
    {
        var start = new ProcessStartInfo(TestModelExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--mcp");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("could not start the test model");
        using var cts = new CancellationTokenSource(Timeout);

        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        var lines = new List<string>();
        var reader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.Length == 0)
                    continue;   // framing keeps messages one per line; blank lines are not frames
                lock (lines)
                    lines.Add(line);
                lock (lines)
                {
                    if (CountResponses(lines) >= expectedResponses)
                        return;
                }
            }
        }, cts.Token);

        foreach (string frame in frames)
            await process.StandardInput.WriteLineAsync(frame.AsMemory(), cts.Token);
        await process.StandardInput.FlushAsync(cts.Token);

        await reader;
        process.StandardInput.Close();          // EOF on stdin ends the server
        await process.WaitForExitAsync(cts.Token);

        return new Session([.. lines], await stderr, process.ExitCode);
    }

    private static int CountResponses(List<string> lines) =>
        lines.Count(l => JsonNode.Parse(l) is JsonObject frame && frame["id"] is not null);

    /// <summary>Locates the built test-model program. The tests project references it
    /// with <c>ReferenceOutputAssembly="false"</c>, so it is built beside us but not
    /// copied in.</summary>
    private static string TestModelExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "tests")
            directory = directory.Parent;
        Assert.NotNull(directory);

        string name = OperatingSystem.IsWindows() ? "EngrCAD.Mcp.TestModel.exe" : "EngrCAD.Mcp.TestModel";
        var candidates = Directory.GetFiles(
            Path.Combine(directory.FullName, "EngrCAD.Mcp.TestModel"), name, SearchOption.AllDirectories);
        Assert.True(candidates.Length > 0, $"'{name}' was not built under {directory.FullName}");
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }
}
