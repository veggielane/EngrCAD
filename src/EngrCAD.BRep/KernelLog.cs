using Microsoft.Extensions.Logging;

namespace EngrCAD.BRep;

/// <summary>
/// BRep's log vocabulary — source-generated templates with stable event IDs in the 90s
/// (the kernel-BRep range; 10s–70s belong to the Viewer/MCP hosts, 80s to
/// EngrCAD.Interop). Opt-in and additive only: <see cref="StepReadResult.Diagnostics"/>
/// remains the import's report — data the caller acts on — and logging complements it.
/// </summary>
internal static partial class KernelLog
{
    [LoggerMessage(EventId = 90, Level = LogLevel.Information,
        Message = "STEP import: {Solids} solid(s), {Instances} instance(s), {Diagnostics} diagnostic(s) in {ElapsedMs:F0} ms")]
    public static partial void StepImported(
        ILogger logger, int solids, int instances, int diagnostics, double elapsedMs);

    [LoggerMessage(EventId = 91, Level = LogLevel.Information,
        Message = "IGES import: {Faces} trimmed surface(s), {Curves} loose curve(s), {Surfaces} untrimmed surface(s), {Diagnostics} diagnostic(s) in {ElapsedMs:F0} ms")]
    public static partial void IgesImported(
        ILogger logger, int faces, int curves, int surfaces, int diagnostics, double elapsedMs);
}
