using System.Globalization;

namespace EngrCAD.Mesh;

/// <summary>Minimal Wavefront OBJ export for debugging meshes in external viewers.</summary>
public static class ObjWriter
{
    public static void Write(HalfEdgeMesh mesh, TextWriter writer)
    {
        var culture = CultureInfo.InvariantCulture;
        var (positions, faces) = mesh.ToIndexed();

        foreach (var p in positions)
            writer.WriteLine(string.Create(culture, $"v {p.X:R} {p.Y:R} {p.Z:R}"));

        foreach (var face in faces)
        {
            writer.Write('f');
            foreach (int v in face)
            {
                writer.Write(' ');
                writer.Write((v + 1).ToString(culture)); // OBJ is 1-based
            }
            writer.WriteLine();
        }
    }

    public static void WriteFile(HalfEdgeMesh mesh, string path)
    {
        using var writer = new StreamWriter(path);
        Write(mesh, writer);
    }
}
