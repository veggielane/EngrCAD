using EngrCAD.BRep;
using EngrCAD.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>The opt-in STEP-import logging seam: event 90 with counts and timing,
/// while <see cref="StepReadResult.Diagnostics"/> stays the import's real report.</summary>
public class KernelLogTests
{
    private sealed class ListLogger : ILogger
    {
        public readonly List<(int Id, LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    [Fact]
    public void StepImport_WithLogger_EmitsEvent90_AndTheSameResult()
    {
        string step = StepWriter.Write(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))));

        var logger = new ListLogger();
        var logged = StepReader.Read(step, logger);
        var silent = StepReader.Read(step);

        var entry = logger.Entries.Single(e => e.Id == 90);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("1 solid(s)", entry.Message);

        // Logging is observation only: the import result is unchanged.
        Assert.Equal(silent.Solids.Count, logged.Solids.Count);
        Assert.Equal(silent.Diagnostics.Count, logged.Diagnostics.Count);
    }
}
