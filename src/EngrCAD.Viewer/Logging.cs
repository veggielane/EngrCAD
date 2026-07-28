using Microsoft.Extensions.Logging;

namespace EngrCAD.Viewer;

/// <summary>
/// Ready-made <see cref="ILogger"/>s for the viewer entry points.
/// <para>EngrCAD logs through <c>Microsoft.Extensions.Logging.Abstractions</c> — the
/// abstractions package only, no provider — so a consumer plugs in whatever sink it
/// already uses and the kernel-projects-carry-no-UI-dependency rule is untouched (a
/// logging abstraction is not UI). Set <see cref="EngrCadOptions.Logger"/>, or
/// <see cref="EngrCadBuilder.WithLogger(ILogger)"/> /
/// <see cref="EngrCadBuilder.WithLoggerFactory"/>.</para>
/// <para><b>Why the default is <see cref="Console"/> and not
/// <c>NullLogger.Instance</c>.</b> A library defaults to silence; a program's front door
/// does not. <see cref="EngrCad.Run"/> IS the front door of a model program, and its
/// "wrote part.step" confirmations and usage errors are that program's console output.
/// Defaulting to null would make <c>dotnet run -- --export part.step</c> print nothing at
/// all, including why it failed. Pass <c>NullLogger.Instance</c> explicitly to get
/// silence — that is the deliberate choice, not the accident.</para>
/// </summary>
public static class EngrCadLoggers
{
    /// <summary>The default sink for the entry points: Information and below to standard
    /// output, Warning and above to standard error — the historical console behavior of
    /// <see cref="EngrCad.Run"/>.</summary>
    public static ILogger Console { get; } = new ConsoleLogger(alwaysStandardError: false);

    /// <summary>Everything to standard error, whatever the level. This is what an MCP
    /// server wants: stdout carries the JSON-RPC frame stream, and clients surface
    /// stderr as server logging.</summary>
    public static ILogger StandardError { get; } = new ConsoleLogger(alwaysStandardError: true);

    /// <summary>The logger to use for <paramref name="options"/> — the configured one, or
    /// <see cref="Console"/> when the caller has not chosen.</summary>
    internal static ILogger Resolve(EngrCadOptions options) => options.Logger ?? Console;

    private sealed class ConsoleLogger(bool alwaysStandardError) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message}: {exception.GetType().Name}: {exception.Message}";

            // Console.Out / Console.Error are read fresh on every call, deliberately:
            // EngrCAD.Mcp's StdoutGuard repoints Console.Out at stderr for the lifetime
            // of a server, and this logger has to follow it there. Caching the writers
            // would reintroduce exactly the stdout corruption the guard exists to stop.
            var writer = alwaysStandardError || logLevel >= LogLevel.Warning
                ? System.Console.Error
                : System.Console.Out;
            writer.WriteLine(message);
        }
    }
}

/// <summary>
/// Every message the viewer entry points emit, as source-generated
/// <see cref="LoggerMessageAttribute"/> methods: message templates with named
/// placeholders (so a structured sink gets fields, not a pre-baked string), no
/// allocation when the level is disabled, and one place to read the whole vocabulary.
/// Event IDs are explicit and stable — sinks and dashboards key on them.
/// </summary>
internal static partial class Log
{
    // ---- command-line usage (exit code 2) ----

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "--render-style requires a style: points, wireframe, shaded, or shaded-edges")]
    internal static partial void UsageRenderStyle(ILogger logger);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error,
        Message = "--section requires an axis (x, y, or z) and a numeric offset, e.g. --section z 6"
                + " (repeat the pair for a quarter or octant cut: --section x 0 y 0)")]
    internal static partial void UsageSection(ILogger logger);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "--ao requires on or off")]
    internal static partial void UsageAmbientOcclusion(ILogger logger);

    [LoggerMessage(EventId = 16, Level = LogLevel.Error,
        Message = "--explode requires a non-negative factor (0 assembled, 1 fully exploded)")]
    internal static partial void UsageExplode(ILogger logger);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error,
        Message = "--mesh requires lazy (mesh each tab when it is first shown) or all"
                + " (mesh the whole document before the window opens)")]
    internal static partial void UsageMeshMode(ILogger logger);

    [LoggerMessage(EventId = 14, Level = LogLevel.Error,
        Message = "--export requires a file path (.step or .obj)")]
    internal static partial void UsageExport(ILogger logger);

    [LoggerMessage(EventId = 15, Level = LogLevel.Error,
        Message = "--render requires a file path (.png)")]
    internal static partial void UsageRender(ILogger logger);

    [LoggerMessage(EventId = 17, Level = LogLevel.Error,
        Message = "--rpc takes an optional port 0-65535 (0 or omitted = ephemeral);"
                + " --rpc-token requires the token value")]
    internal static partial void UsageRpc(ILogger logger);

    // ---- headless export / render ----

    [LoggerMessage(EventId = 20, Level = LogLevel.Information,
        Message = "wrote {Path} ({PartCount} part(s))")]
    internal static partial void WroteImage(ILogger logger, string path, int partCount);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information,
        Message = "wrote {Path} ({InstanceCount} instance(s), merged)")]
    internal static partial void WroteObj(ILogger logger, string path, int instanceCount);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information,
        Message = "wrote {Path} ({InstanceCount} instance(s), merged binary STL)")]
    internal static partial void WroteStl(ILogger logger, string path, int instanceCount);

    [LoggerMessage(EventId = 23, Level = LogLevel.Information, Message = "wrote {Path} ('{PartName}')")]
    internal static partial void WroteStep(ILogger logger, string path, string partName);

    [LoggerMessage(EventId = 24, Level = LogLevel.Error, Message = "The scene has no parts to render.")]
    internal static partial void NothingToRender(ILogger logger);

    [LoggerMessage(EventId = 25, Level = LogLevel.Error, Message = "The scene has no parts to export.")]
    internal static partial void NothingToExport(ILogger logger);

    [LoggerMessage(EventId = 26, Level = LogLevel.Error,
        Message = "Unsupported render format '{Extension}' — use .png.")]
    internal static partial void UnsupportedRenderFormat(ILogger logger, string extension);

    [LoggerMessage(EventId = 27, Level = LogLevel.Error,
        Message = "Unsupported export format '{Extension}' — use .step, .stl, .obj, .3mf, .amf, or .off.")]
    internal static partial void UnsupportedExportFormat(ILogger logger, string extension);

    [LoggerMessage(EventId = 28, Level = LogLevel.Error,
        Message = "Offscreen rendering is not available: {Reason}")]
    internal static partial void OffscreenUnavailable(ILogger logger, string? reason);

    /// <summary>A partial success: the other parts still export, so this is a warning,
    /// not an error — and the console sink still puts it on stderr.</summary>
    [LoggerMessage(EventId = 29, Level = LogLevel.Warning,
        Message = "skipping '{PartName}': not B-Rep-representable (STEP needs exact solids)")]
    internal static partial void SkippingNonBrepPart(ILogger logger, string partName);

    [LoggerMessage(EventId = 30, Level = LogLevel.Error,
        Message = "No B-Rep-representable parts; nothing exported.")]
    internal static partial void NoBrepParts(ILogger logger);

    [LoggerMessage(EventId = 31, Level = LogLevel.Information,
        Message = "wrote {Path} (STEP assembly: {ProductCount} product(s), {InstanceCount} occurrence(s))")]
    internal static partial void WroteStepAssembly(
        ILogger logger, string path, int productCount, int instanceCount);

    /// <summary>The mesh-format exports that carry a format name (3MF, AMF, OFF) — one
    /// template rather than an event per extension.</summary>
    [LoggerMessage(EventId = 32, Level = LogLevel.Information,
        Message = "wrote {Path} ({InstanceCount} instance(s), {Format})")]
    internal static partial void WroteMeshFormat(
        ILogger logger, string path, int instanceCount, string format);

    // ---- live modeling loop ----

    [LoggerMessage(EventId = 40, Level = LogLevel.Error,
        Message = "model error: {Error} (showing empty scene)")]
    internal static partial void ModelErrorAtStartup(ILogger logger, string error);

    [LoggerMessage(EventId = 41, Level = LogLevel.Error,
        Message = "model error: {Error} (keeping last good scene)")]
    internal static partial void ModelErrorOnReload(ILogger logger, string error);

    [LoggerMessage(EventId = 42, Level = LogLevel.Information,
        Message = "reloaded at {Time:HH:mm:ss} — {PartCount} part(s)")]
    internal static partial void Reloaded(ILogger logger, DateTime time, int partCount);

    // ---- display ----

    [LoggerMessage(EventId = 50, Level = LogLevel.Error,
        Message = "part '{PartName}' failed to mesh: {Reason}")]
    internal static partial void PartFailedToMesh(ILogger logger, string partName, string reason);

    // ---- remote control (70s; the MCP server owns the 60s) ----

    [LoggerMessage(EventId = 70, Level = LogLevel.Information,
        Message = "remote control listening on 127.0.0.1:{Port}{TokenNote}")]
    internal static partial void RemoteControlListening(ILogger logger, int port, string tokenNote);

    [LoggerMessage(EventId = 71, Level = LogLevel.Error,
        Message = "remote control failed to start: {Reason}")]
    internal static partial void RemoteControlFailed(ILogger logger, string reason);
}
