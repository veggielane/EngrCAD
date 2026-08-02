using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace EngrCAD.Mcp.Tests;

/// <summary>
/// The remote-control path against a <b>real window</b>: a child process running a design
/// program with <c>--view --rpc 0</c>, driven through the MCP bridge tools over its
/// loopback socket.
///
/// <para><b>Why this exists.</b> Everything either side of the window is locked
/// headlessly — the transport and vocabulary over real sockets with a stub viewer
/// (<c>ViewerBridgeTests</c>), and <c>ViewportRemoteViewer</c>'s arm-on-UI-thread /
/// wait-off-it split and its deadline against a real <c>ViewportControl</c> with no
/// window (<c>ViewportRemoteViewerTests</c> in EngrCAD.Viewer.Tests). What neither can
/// reach is a real GL <b>render pass</b> claiming the armed capture and reaching
/// <c>WriteCapture</c>, and a real Avalonia dispatcher servicing the marshaled calls.
/// This drives exactly that, and returns the frame as bytes.</para>
///
/// <para><b>Opt-in, and the reason is honest rather than technical.</b> It opens a
/// desktop window, which on a developer's machine steals focus mid-suite; a GUI popping
/// up on every <c>dotnet test</c> is a real cost to pay on every run for a leg that
/// changes rarely. Set <c>ENGRCAD_WINDOWED_TESTS=1</c> to run it — that IS the "one
/// manual pass per release" todo.md asked for, reduced to one command:
/// <code>
/// $env:ENGRCAD_WINDOWED_TESTS = "1"
/// dotnet test tests/EngrCAD.Mcp.Tests --filter WindowedRpcTests
/// </code>
/// </para>
/// </summary>
[Collection("offscreen-gl")]
public partial class WindowedRpcTests
{
    private const string OptIn = "ENGRCAD_WINDOWED_TESTS";
    private static readonly TimeSpan Startup = TimeSpan.FromMinutes(2);

    [SkippableFact]
    public async Task A_live_window_serves_the_vocabulary_and_captures_a_real_frame()
    {
        Skip.If(Environment.GetEnvironmentVariable(OptIn) is not ("1" or "true"),
            $"windowed test: set {OptIn}=1 to run it (it opens a desktop window).");

        using var viewer = await LiveViewer.StartAsync("--view", "--rpc", "0", "--mesh", "all", "--animate");
        var tools = new ViewerTools(new ViewerRpcClient(viewer.Port));

        // 1. The endpoint is the program's own: title and vocabulary come from the window.
        var ping = await viewer.Client.SendAsync("ping");
        Assert.Equal("mcp test model", (string?)ping!["title"]);
        Assert.Contains("set_animation_time", ping["methods"]!.AsArray().Select(m => (string?)m));

        // 2. The instance list is the WINDOW's, not the scene's — and that distinction is
        //    measurable here in a way no stub can show: `ViewportControl.InstancePaths`
        //    reads `_instances`, which the RENDER PASS swaps in from `_pending`, so a
        //    client connecting the instant the port is announced legitimately sees an
        //    EMPTY list (measured: it does, every run — the port is reported from
        //    OnViewportReady, before the first frame). The readiness probe that
        //    measurement forced is what a client polls now: ping carries "ready"
        //    (ViewportControl.InstancesDisplayed — true once the render pass has
        //    adopted the swap), and the list read AFTER ready is complete with no
        //    blind retry. Reporting the pending list instead was rejected because it
        //    would desync the paths from the INDICES select_part and set_display_mode
        //    address.
        await Retry(() => Assert.True(
            (bool?)viewer.Client.SendAsync("ping").GetAwaiter().GetResult()!["ready"],
            "the window has not adopted its instances yet"));
        var parts = viewer.Client.SendAsync("list_parts").GetAwaiter().GetResult()!
            .AsArray().Select(p => (string?)p).OfType<string>().ToList();
        Assert.Contains("bracket", parts);
        Assert.Contains("pin", parts);

        // 3. Mutations marshal onto a live dispatcher and stick.
        Assert.NotEqual(true, tools.SetView("front").IsError);
        Assert.NotEqual(true, tools.SetViewStyle("shaded-edges").IsError);
        Assert.NotEqual(true, tools.SelectPart("pin").IsError);
        Assert.Contains("pin", Text(tools.GetSelection()));

        // 4. THE LEG: a real render pass reads the framebuffer back, the write completes
        //    the capture, and the bridge reads the bytes. A PNG that decodes and is bigger
        //    than a blank one is the proof the render pass ran at all.
        var shot = tools.Screenshot(Path.Combine(Path.GetTempPath(), $"engrcad-win-{Guid.NewGuid():N}.png"));
        Assert.NotEqual(true, shot.IsError);
        byte[] png = Assert.Single(shot.Content.OfType<ImageContentBlock>()).DecodedData.ToArray();
        Assert.Equal<byte[]>([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        var (width, height) = PngSize(png);
        Assert.True(width > 200 && height > 200, $"the captured frame is {width}x{height}");
        Assert.True(png.Length > 4000, $"a {width}x{height} window frame should not be {png.Length} bytes");

        // 5. The animation parity gap, closed end to end: the transport arms on a
        //    background task, so the first seek may legitimately arrive before it exists.
        await Retry(() => Assert.NotEqual(true, tools.SetAnimationTime(0).IsError));
        byte[] atZero = Capture(tools);

        var parked = tools.SetAnimationTime(0.5);
        Assert.NotEqual(true, parked.IsError);
        // The window's own arithmetic, not the request echoed: SceneHost clamps and
        // divides by the animation's duration, so a wrong duration would show up here.
        Assert.Contains("0.5", Text(parked));
        Assert.Contains("paused", Text(parked));
        byte[] atHalf = Capture(tools);

        // Half a turntable turn is the far side of the model — the claim that the playback
        // position reached the RENDER, which no in-process assertion can make. (Only this
        // direction is asserted: two live-composited frames of the same pose need not be
        // byte-identical, since a background ambient-occlusion bake can land between them.)
        Assert.NotEqual(atZero, atHalf);
    }

    [SkippableFact]
    public async Task A_window_without_an_animation_refuses_a_seek_by_name()
    {
        Skip.If(Environment.GetEnvironmentVariable(OptIn) is not ("1" or "true"),
            $"windowed test: set {OptIn}=1 to run it (it opens a desktop window).");

        // Same program, no --animate: the refusal has to come from the real SceneHost
        // (whose _playback is null) rather than from a stub that was told to say no.
        using var viewer = await LiveViewer.StartAsync("--view", "--rpc", "0", "--mesh", "all");
        var tools = new ViewerTools(new ViewerRpcClient(viewer.Port));

        var refused = tools.SetAnimationTime(0.5);
        Assert.True(refused.IsError == true);
        Assert.Contains("no animation", Text(refused));
        // The window is otherwise fine, which is what makes it a refusal and not a fault.
        Assert.NotEqual(true, tools.Fit().IsError);
    }

    // ---- driving the window ----

    private static byte[] Capture(ViewerTools tools)
    {
        var shot = tools.Screenshot(Path.Combine(Path.GetTempPath(), $"engrcad-win-{Guid.NewGuid():N}.png"));
        Assert.NotEqual(true, shot.IsError);
        return Assert.Single(shot.Content.OfType<ImageContentBlock>()).DecodedData.ToArray();
    }

    /// <summary>Retries an assertion while the window finishes standing itself up — the
    /// instances land on the first RENDER PASS and the animation transport is armed on a
    /// background task, so either can legitimately be absent when the port is announced.
    /// A deadline rather than a sleep: nothing here is timing-sensitive once it has
    /// landed, and the last failure is what surfaces if it never does.</summary>
    private static async Task<T> Retry<T>(Func<T> assertion)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return assertion();
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    private static Task Retry(Action assertion) => Retry(() => { assertion(); return 0; });

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static (int Width, int Height) PngSize(byte[] png) =>
        ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19],
         (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);

    /// <summary>A running viewer process with its remote-control port read off the log
    /// line the endpoint prints when it binds (event 70) — the same line a human reads
    /// before passing <c>--viewer &lt;port&gt;</c> to the MCP server.</summary>
    private sealed partial class LiveViewer : IDisposable
    {
        [GeneratedRegex(@"remote control listening on 127\.0\.0\.1:(\d+)")]
        private static partial Regex PortLine();

        private readonly Process _process;

        private LiveViewer(Process process, int port)
        {
            _process = process;
            Port = port;
        }

        public int Port { get; }

        public ViewerRpcClient Client => field ??= new ViewerRpcClient(Port);

        public static async Task<LiveViewer> StartAsync(params string[] args)
        {
            var start = new ProcessStartInfo(TestModelProgram.Executable())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (string argument in args)
                start.ArgumentList.Add(argument);

            var process = Process.Start(start)
                ?? throw new InvalidOperationException("could not start the test model");
            var log = new StringBuilder();
            using var cts = new CancellationTokenSource(Startup);
            try
            {
                while (await process.StandardOutput.ReadLineAsync(cts.Token) is { } line)
                {
                    log.AppendLine(line);
                    if (PortLine().Match(line) is { Success: true } match)
                    {
                        var viewer = new LiveViewer(process, int.Parse(match.Groups[1].ValueSpan));
                        // Keep DRAINING both pipes for the rest of the run. A redirected
                        // stream nobody reads fills its buffer, and the next write blocks
                        // the writer — here that is the viewer's own logging on its UI
                        // thread, so the window would stop rendering and every RPC call
                        // would sit out its timeout. Cheap insurance against a hang whose
                        // cause is nowhere near where it shows up.
                        viewer.Drain(process.StandardOutput);
                        viewer.Drain(process.StandardError);
                        return viewer;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // fall through to the failure below with whatever was logged
            }
            process.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException(
                "the viewer never reported a remote-control port. Its output was:\n"
                + log + "\n" + await process.StandardError.ReadToEndAsync());
        }

        private void Drain(StreamReader stream) =>
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await stream.ReadLineAsync() is not null) { }
                }
                catch (Exception) { }   // the process is killed out from under it at Dispose
            });

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            _process.Dispose();
        }
    }
}
