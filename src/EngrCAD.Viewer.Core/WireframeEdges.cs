using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Viewer;

/// <summary>
/// Extracts every unique mesh edge as a line segment for wireframe display —
/// the half-edge structure already knows each edge exactly once.
/// <para>
/// Here rather than in EngrCAD.Viewer because it is render-model geometry with no GL in
/// it, and every front end that offers a wireframe needs the same segments. A browser
/// client with its own edge walk would be the drift this assembly exists to prevent —
/// and the walk order decides the vertex order in the uploaded buffer, so two copies
/// would not even upload the same bytes.
/// </para>
/// </summary>
public static class WireframeEdges
{
    /// <summary>Every edge of <paramref name="mesh"/> once, as an endpoint pair.</summary>
    public static List<(Vector3d A, Vector3d B)> Extract(HalfEdgeMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var segments = new List<(Vector3d, Vector3d)>(mesh.EdgeCount);
        foreach (var edge in mesh.Edges)
            segments.Add((edge.Origin.Position, edge.Destination.Position));
        return segments;
    }
}
