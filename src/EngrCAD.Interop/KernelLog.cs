using Microsoft.Extensions.Logging;

namespace EngrCAD.Interop;

/// <summary>
/// Interop's log vocabulary — source-generated templates with stable event IDs in the
/// 80s (the kernel-Interop range; 10s–70s belong to the Viewer/MCP hosts, 90s to
/// EngrCAD.BRep). Logging here is strictly OPT-IN and additive: every operation's
/// findings remain return values or exceptions, and a null logger costs one branch.
/// Levels: a whole boolean is Information (the unit a caller reasons about); the
/// tessellations and SDF builds inside it are Debug (sub-steps, several per boolean).
/// </summary>
internal static partial class KernelLog
{
    [LoggerMessage(EventId = 80, Level = LogLevel.Information,
        Message = "B-Rep {Operation}: {FacesA}+{FacesB} faces -> {FacesOut} in {ElapsedMs:F0} ms")]
    public static partial void BooleanCompleted(
        ILogger logger, string operation, int facesA, int facesB, int facesOut, double elapsedMs);

    [LoggerMessage(EventId = 81, Level = LogLevel.Debug,
        Message = "tessellated {Faces} faces -> {Triangles} triangles in {ElapsedMs:F0} ms")]
    public static partial void TessellationCompleted(
        ILogger logger, int faces, int triangles, double elapsedMs);

    [LoggerMessage(EventId = 82, Level = LogLevel.Debug,
        Message = "mesh SDF built over {Triangles} triangles ({SignSource}) in {ElapsedMs:F0} ms")]
    public static partial void MeshSdfBuilt(
        ILogger logger, int triangles, MeshSignSource signSource, double elapsedMs);
}
