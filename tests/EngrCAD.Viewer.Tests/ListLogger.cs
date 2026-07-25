using Microsoft.Extensions.Logging;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// An <see cref="ILogger"/> that keeps what it was told, split by level the way the
/// console sink splits streams: <see cref="Infos"/> is everything below Warning,
/// <see cref="Errors"/> is Warning and above. Replaces the old delegate/interface shim
/// in tests — with <c>ILogger</c> the capture is just an implementation.
/// </summary>
internal sealed class ListLogger : ILogger
{
    public List<string> Infos { get; } = [];

    public List<string> Errors { get; } = [];

    /// <summary>Every message in arrival order, whatever the level.</summary>
    public List<string> All { get; } = [];

    /// <summary>The event IDs seen, in order — the stable contract a sink keys on.</summary>
    public List<int> EventIds { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception is not null)
            message = $"{message}: {exception.GetType().Name}: {exception.Message}";
        All.Add(message);
        EventIds.Add(eventId.Id);
        (logLevel >= LogLevel.Warning ? Errors : Infos).Add(message);
    }
}
