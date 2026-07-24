using EngrCAD.Core;
using EngrCAD.Mesh;

namespace EngrCAD.Viewer;

/// <summary>
/// Extracts every unique mesh edge as a line segment for wireframe display —
/// the half-edge structure already knows each edge exactly once.
/// </summary>
internal static class WireframeEdges
{
    public static List<(Vector3d A, Vector3d B)> Extract(HalfEdgeMesh mesh)
    {
        var segments = new List<(Vector3d, Vector3d)>(mesh.EdgeCount);
        foreach (var edge in mesh.Edges)
            segments.Add((edge.Origin.Position, edge.Destination.Position));
        return segments;
    }
}
