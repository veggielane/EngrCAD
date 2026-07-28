using System.Globalization;
using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// OFF (Object File Format) export — the writer twin of <see cref="OffReader"/>:
/// `OFF` header, a vertex/face/edge count line (edge count written as 0, which the
/// format permits), vertex lines, then `k i0 ... i(k-1)` face lines. Polygonal faces
/// are written as-is (OFF supports n-gons); multi-part output is merged with each
/// part's transform applied, mirroring <see cref="StlWriter"/>'s contract — a
/// negative-determinant transform reverses face windings so faces stay outward.
/// Round-trips through <see cref="OffReader"/> exactly (R-format doubles).
/// </summary>
public static class OffWriter
{
    public static void Write(HalfEdgeMesh mesh, TextWriter writer) =>
        Write([(mesh, Matrix4d.Identity)], writer);

    /// <summary>Writes several meshes into one merged OFF, applying a transform to
    /// each.</summary>
    public static void Write(IReadOnlyList<(HalfEdgeMesh Mesh, Matrix4d Transform)> parts, TextWriter writer)
    {
        var culture = CultureInfo.InvariantCulture;
        int vertexTotal = 0, faceTotal = 0;
        var indexed = new (Vector3d[] Positions, List<int[]> Faces, bool Flip)[parts.Count];
        for (int p = 0; p < parts.Count; p++)
        {
            var (mesh, transform) = parts[p];
            var (positions, faces) = mesh.ToIndexed();
            var world = new Vector3d[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                world[i] = transform.TransformPoint(positions[i]);
            indexed[p] = (world, faces, transform.Determinant < 0);
            vertexTotal += world.Length;
            faceTotal += faces.Count;
        }

        writer.WriteLine("OFF");
        writer.WriteLine(string.Create(culture, $"{vertexTotal} {faceTotal} 0"));
        foreach (var (positions, _, _) in indexed)
        {
            foreach (var p in positions)
                writer.WriteLine(string.Create(culture, $"{p.X:R} {p.Y:R} {p.Z:R}"));
        }

        int offset = 0;
        foreach (var (positions, faces, flip) in indexed)
        {
            foreach (var face in faces)
            {
                writer.Write(face.Length.ToString(culture));
                // A mirrored part's faces reverse winding; rotate the reversal so
                // vertex 0 stays first (the fan-diagonal lesson: [a, d, c, b]) so a
                // consumer fanning from the first vertex keeps the same diagonal.
                for (int i = 0; i < face.Length; i++)
                {
                    int index = flip && i > 0 ? face[face.Length - i] : face[i];
                    writer.Write(' ');
                    writer.Write((index + offset).ToString(culture));
                }
                writer.WriteLine();
            }
            offset += positions.Length;
        }
    }

    public static void WriteFile(HalfEdgeMesh mesh, string path)
    {
        using var writer = new StreamWriter(path);
        Write(mesh, writer);
    }

    public static void WriteFile(IReadOnlyList<(HalfEdgeMesh Mesh, Matrix4d Transform)> parts, string path)
    {
        using var writer = new StreamWriter(path);
        Write(parts, writer);
    }
}
