using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The opt-in kernel logging seam: long operations take an optional ILogger and emit
/// stable event IDs (80 boolean, 81 tessellation, 82 mesh SDF) WITHOUT changing any
/// result — a null logger is the default everywhere and costs one branch.
/// </summary>
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

    private static BrepSolid BoxA() => SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
    private static BrepSolid BoxB() => SolidFactory.MakeBox(new Aabb((1, 1, 1), (3, 3, 3)));

    [Fact]
    public void Boolean_WithLogger_EmitsStableEventIds_AndTheSameResult()
    {
        var logger = new ListLogger();
        var logged = BrepBoolean.Union(BoxA(), BoxB(), logger);
        var silent = BrepBoolean.Union(BoxA(), BoxB());

        // The whole boolean is one Information entry (event 80) naming the operation...
        var completion = logger.Entries.Single(e => e.Id == 80);
        Assert.Equal(LogLevel.Information, completion.Level);
        Assert.Contains("Union", completion.Message);

        // ...and its sub-steps (two tessellations, two SDF builds for classification)
        // ride the same logger at Debug.
        Assert.Equal(2, logger.Entries.Count(e => e.Id == 81));
        Assert.Equal(2, logger.Entries.Count(e => e.Id == 82));
        Assert.All(logger.Entries.Where(e => e.Id is 81 or 82),
            e => Assert.Equal(LogLevel.Debug, e.Level));

        // Logging is observation only: the geometry is identical with and without.
        Assert.Equal(
            BRepTessellator.Tessellate(silent).Volume(),
            BRepTessellator.Tessellate(logged).Volume());
    }

    [Fact]
    public void DisjointFastPath_LogsItsCompletionToo()
    {
        var logger = new ListLogger();
        var apart = SolidFactory.MakeBox(new Aabb((10, 10, 10), (12, 12, 12)));
        BrepBoolean.Union(BoxA(), apart, logger);
        Assert.Single(logger.Entries, e => e.Id == 80);
    }

    [Fact]
    public void Tessellate_WithLogger_ReportsFacesAndTriangles()
    {
        var logger = new ListLogger();
        var mesh = BRepTessellator.Tessellate(BoxA(), logger: logger);
        var entry = logger.Entries.Single(e => e.Id == 81);
        Assert.Contains("6 faces", entry.Message);
        Assert.Contains($"{mesh.FaceCount} triangles", entry.Message);
    }

    [Fact]
    public void MeshSdf_WithLogger_ReportsItsBuild()
    {
        var logger = new ListLogger();
        _ = new MeshSdf(BRepTessellator.Tessellate(BoxA()), logger);
        var entry = logger.Entries.Single(e => e.Id == 82);
        Assert.Contains("12 triangles", entry.Message);
        Assert.Contains("Pseudonormal", entry.Message);
    }
}
