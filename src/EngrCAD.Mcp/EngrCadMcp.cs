using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Microsoft.Extensions.Logging;

namespace EngrCAD.Mcp;

/// <summary>
/// The entry point for a design program that wants to be usable by an AI assistant:
/// <code>
/// return EngrCadMcp.Run(args, BuildScene, "my bracket");
/// </code>
/// It adds one switch, <c>--mcp</c>, which serves the program's scene over the Model
/// Context Protocol on stdio instead of opening a window; every other argument is
/// handed straight to <see cref="EngrCad.Run"/>, so <c>--view</c>, <c>--export</c>,
/// <c>--render</c> and the <c>dotnet watch</c> live loop are unchanged.
/// <para>An assistant is then configured with</para>
/// <code>
/// { "command": "dotnet", "args": ["run", "--project", "MyModel", "--", "--mcp"] }
/// </code>
/// <para>Nothing in this package is referenced by the kernel or by the viewer's
/// required surface — a consumer that never wants a protocol stack simply never
/// references <c>EngrCAD.Mcp</c>.</para>
/// </summary>
public static class EngrCadMcp
{
    /// <summary>The switch that selects MCP-over-stdio mode.</summary>
    public const string Switch = "--mcp";

    /// <summary>With <see cref="Switch"/>: <c>--viewer &lt;port&gt;</c> bridges to a
    /// RUNNING viewer window's remote-control endpoint (the model started separately
    /// with <c>--rpc &lt;port&gt;</c>), adding the live tools (set_view, fit,
    /// set_section, set_display_mode, set_view_style, select_part, get_selection,
    /// measure, viewer_screenshot). <c>--viewer-token</c> passes the shared token.</summary>
    public const string ViewerSwitch = "--viewer";

    /// <summary>Standard main-method wrapper: <c>--mcp</c> serves the scene over the
    /// Model Context Protocol on stdio; anything else falls through to
    /// <see cref="EngrCad.Run"/>. Returns a process exit code.</summary>
    public static int Run(string[] args, Func<Scene> sceneFactory, string title = "EngrCAD") =>
        Run(args, sceneFactory, new EngrCadOptions { Title = title });

    /// <summary><see cref="Run(string[], Func{Scene}, string)"/> with host-level
    /// options (mesh quality, render size, log sink) — the same
    /// <see cref="EngrCadOptions"/> the viewer's fluent builder accumulates.</summary>
    public static int Run(string[] args, Func<Scene> sceneFactory, EngrCadOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(sceneFactory);
        ArgumentNullException.ThrowIfNull(options);

        if (!args.Contains(Switch, StringComparer.Ordinal))
            return EngrCad.Configure(options).Run(args, sceneFactory);

        // --viewer <port> (+ --viewer-token <t>): bridge tools into a running window.
        ViewerRpcClient? viewer = null;
        int viewerIndex = Array.IndexOf(args, ViewerSwitch);
        if (viewerIndex >= 0)
        {
            if (viewerIndex + 1 >= args.Length
                || !int.TryParse(args[viewerIndex + 1], out int port) || port is <= 0 or > 65535)
            {
                (options.Logger ?? EngrCadLoggers.StandardError).LogError(
                    "--viewer requires the port the viewer's --rpc endpoint reported (1-65535)");
                return 2;
            }
            string? token = null;
            int tokenIndex = Array.IndexOf(args, "--viewer-token");
            if (tokenIndex >= 0)
            {
                if (tokenIndex + 1 >= args.Length)
                {
                    (options.Logger ?? EngrCadLoggers.StandardError).LogError(
                        "--viewer-token requires the token value");
                    return 2;
                }
                token = args[tokenIndex + 1];
            }
            viewer = new ViewerRpcClient(port, token);
        }
        return Serve(sceneFactory, options, viewer);
    }

    /// <summary>
    /// Serves the scene over MCP on this process's stdin/stdout and blocks until the
    /// client disconnects. Called by <see cref="Run(string[], Func{Scene}, EngrCadOptions)"/>
    /// for <c>--mcp</c>; public so a custom host can start the server itself.
    /// <para><b>stdout becomes the protocol channel</b> — see <see cref="StdoutGuard"/>.
    /// Console output from anywhere in the process is redirected to stderr for the
    /// lifetime of the server, and the log seam is pointed at stderr too when the
    /// caller has not configured one. Building the scene happens after that
    /// redirection, so a model that prints while it builds cannot corrupt the stream.</para>
    /// </summary>
    public static int Serve(Func<Scene> sceneFactory, EngrCadOptions options) =>
        Serve(sceneFactory, options, viewer: null);

    /// <summary><see cref="Serve(Func{Scene}, EngrCadOptions)"/> plus the optional
    /// live-viewer bridge (<paramref name="viewer"/> points at a running window's
    /// <c>--rpc</c> endpoint; the bridge tools are added to the served surface).</summary>
    public static int Serve(Func<Scene> sceneFactory, EngrCadOptions options, ViewerRpcClient? viewer)
    {
        ArgumentNullException.ThrowIfNull(sceneFactory);
        ArgumentNullException.ThrowIfNull(options);

        using var guard = StdoutGuard.Claim();
        // Only when the caller did not configure a sink. The default console logger
        // already resolves Console.Out per call, which the guard has pointed at stderr —
        // but naming the stderr sink explicitly means a future default cannot quietly
        // reintroduce the bug.
        options.Logger ??= EngrCadLoggers.StandardError;
        var log = options.Logger;

        SceneSession session;
        try
        {
            // The scene is built here — after the redirection — so its own printing is safe.
            session = new SceneSession(sceneFactory, options.Quality, options.Animation);
        }
        catch (Exception e)
        {
            McpLog.ModelError(log, e);
            return 1;
        }

        var tools = new SceneTools(session);
        McpLog.Serving(log, options.Title, session.Scene.Tabs.Count, session.Scene.AllParts.Count());

        try
        {
            EngrCadMcpServer.RunAsync(
                Console.OpenStandardInput(), guard.ProtocolOutput, tools, options.Title,
                viewer is null ? null : new ViewerTools(viewer))
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e)
        {
            McpLog.ServerError(log, e);
            return 1;
        }
    }
}

/// <summary>The MCP server's log vocabulary — source-generated message templates, like
/// the viewer's. The exception overloads carry the exception itself rather than a
/// pre-formatted string, so a structured sink keeps the type and stack.</summary>
internal static partial class McpLog
{
    [LoggerMessage(EventId = 60, Level = LogLevel.Error, Message = "model error")]
    internal static partial void ModelError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 61, Level = LogLevel.Information,
        Message = "engrcad mcp: serving '{Title}' ({TabCount} tab(s), {PartCount} part(s)) on stdio")]
    internal static partial void Serving(ILogger logger, string title, int tabCount, int partCount);

    [LoggerMessage(EventId = 62, Level = LogLevel.Error, Message = "mcp server error")]
    internal static partial void ServerError(ILogger logger, Exception exception);
}
