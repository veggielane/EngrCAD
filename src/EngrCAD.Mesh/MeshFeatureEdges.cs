using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// Extracts the visually significant edges of a mesh: boundary edges and edges whose
/// faces meet at a sharp dihedral. Viewers overlay these as dark lines — the classic
/// shaded-with-edges CAD look — without wireframing every triangle.
/// </summary>
public static class MeshFeatureEdges
{
    /// <summary>
    /// Segments (endpoint pairs) of all boundary edges plus interior edges deviating
    /// from flat by more than <paramref name="sharpAngleRadians"/> (default 30°).
    /// </summary>
    public static List<(Vector3d A, Vector3d B)> Extract(
        HalfEdgeMesh mesh, double sharpAngleRadians = Math.PI / 6)
    {
        var segments = new List<(Vector3d, Vector3d)>();
        double flatLimit = Math.PI - sharpAngleRadians;
        foreach (var edge in mesh.Edges)
        {
            bool sharp = edge.IsBoundaryEdge || edge.DihedralAngle() < flatLimit;
            if (sharp)
                segments.Add((edge.Origin.Position, edge.Destination.Position));
        }
        return segments;
    }
}
