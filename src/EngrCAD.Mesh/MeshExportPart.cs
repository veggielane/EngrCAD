using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// One named, posed, optionally colored mesh handed to the multi-part exporters
/// (<see cref="ThreeMfWriter"/>, <see cref="AmfWriter"/>) — the same
/// (mesh, transform) pair <see cref="StlWriter"/> takes, plus the name and display
/// color those richer formats can carry. UI-framework free: the color is plain RGB in
/// [0, 1] (callers with a display color convert; null omits material information).
/// </summary>
public readonly record struct MeshExportPart(
    HalfEdgeMesh Mesh, Matrix4d Transform, string Name, (float R, float G, float B)? Color = null)
{
    public MeshExportPart(HalfEdgeMesh mesh, string name)
        : this(mesh, Matrix4d.Identity, name) { }
}
