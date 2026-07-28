using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>Binary STL export (the 3D-printing interchange format): 80-byte header,
/// triangle count, then 50 bytes per facet. N-gon faces are fan-triangulated.</summary>
public static class StlWriter
{
    public static void Write(HalfEdgeMesh mesh, Stream stream) => Write([(mesh, Matrix4d.Identity)], stream);

    /// <summary>Writes several meshes into one STL, applying a transform to each
    /// (winding flipped under mirroring so facets stay outward).</summary>
    public static void Write(IReadOnlyList<(HalfEdgeMesh Mesh, Matrix4d Transform)> parts, Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var header = new byte[80];
        System.Text.Encoding.ASCII.GetBytes("EngrCAD binary STL").CopyTo(header, 0);
        writer.Write(header);

        uint count = 0;
        foreach (var (mesh, _) in parts)
        {
            foreach (var face in mesh.Faces)
                count += (uint)(face.Degree - 2);
        }
        writer.Write(count);

        foreach (var (mesh, transform) in parts)
        {
            bool flip = transform.Determinant < 0;
            var (positions, faces) = mesh.ToIndexed();
            var world = new Vector3d[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                world[i] = transform.TransformPoint(positions[i]);

            foreach (var face in faces)
            {
                // The shared fan rule — a quad splits along its shorter 3D diagonal —
                // read on the TRANSFORMED points, since a non-uniform scale can swap
                // which diagonal is shorter and the exported solid must be the one the
                // viewer draws in world space.
                int apex = PolygonFan.Apex(face, world);
                for (int i = 1; i + 1 < face.Length; i++)
                {
                    var a = world[face[apex]];
                    var b = world[face[PolygonFan.Corner(apex, face.Length, flip ? i + 1 : i)]];
                    var c = world[face[PolygonFan.Corner(apex, face.Length, flip ? i : i + 1)]];
                    var normal = (b - a).Cross(c - a);
                    normal = normal.TryNormalize(Tolerance.Default, out var n) ? n : Vector3d.Zero;

                    WriteVector(writer, normal);
                    WriteVector(writer, a);
                    WriteVector(writer, b);
                    WriteVector(writer, c);
                    writer.Write((ushort)0); // attribute byte count
                }
            }
        }
    }

    public static void WriteFile(HalfEdgeMesh mesh, string path)
    {
        using var stream = File.Create(path);
        Write(mesh, stream);
    }

    public static void WriteFile(IReadOnlyList<(HalfEdgeMesh Mesh, Matrix4d Transform)> parts, string path)
    {
        using var stream = File.Create(path);
        Write(parts, stream);
    }

    private static void WriteVector(BinaryWriter writer, in Vector3d v)
    {
        writer.Write((float)v.X);
        writer.Write((float)v.Y);
        writer.Write((float)v.Z);
    }
}
