using EngrCAD.Modeling;
using EngrCAD.Viewer;

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

        return args.Contains(Switch, StringComparer.Ordinal)
            ? Serve(sceneFactory, options)
            : EngrCad.Configure(options).Run(args, sceneFactory);
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
    public static int Serve(Func<Scene> sceneFactory, EngrCadOptions options)
    {
        ArgumentNullException.ThrowIfNull(sceneFactory);
        ArgumentNullException.ThrowIfNull(options);

        using var guard = StdoutGuard.Claim();
        // Only when the caller did not configure a sink: EngrCadLog.Console writes Info
        // with Console.WriteLine, which the guard has already sent to stderr, but being
        // explicit means a future default cannot quietly reintroduce the bug.
        options.Log ??= EngrCadLog.From(message => Console.Error.WriteLine(message));
        var log = options.Log;

        SceneSession session;
        try
        {
            // The scene is built here — after the redirection — so its own printing is safe.
            session = new SceneSession(sceneFactory, options.Quality);
        }
        catch (Exception e)
        {
            log.Error($"model error: {e.GetType().Name}: {e.Message}");
            return 1;
        }

        var tools = new SceneTools(session);
        log.Info($"engrcad mcp: serving '{options.Title}' "
               + $"({session.Scene.Tabs.Count} tab(s), {session.Scene.AllParts.Count()} part(s)) on stdio");

        try
        {
            EngrCadMcpServer.RunAsync(
                Console.OpenStandardInput(), guard.ProtocolOutput, tools, options.Title)
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e)
        {
            log.Error($"mcp server error: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }
}
