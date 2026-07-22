using EngrCAD.Core;

namespace EngrCAD.Mesh;

/// <summary>
/// GPU-ready triangle data extracted from a <see cref="HalfEdgeMesh"/>: flat float arrays,
/// n-gons fan-triangulated. Flat variant duplicates vertices per face for faceted CAD
/// shading; smooth variant shares vertices with area-weighted normals.
/// </summary>
public sealed class RenderMesh
{
    /// <summary>xyz triples.</summary>
    public required float[] Positions { get; init; }

    /// <summary>xyz triples, same length as <see cref="Positions"/>.</summary>
    public required float[] Normals { get; init; }

    public required uint[] Indices { get; init; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;

    public static RenderMesh CreateFlat(HalfEdgeMesh mesh)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<uint>();

        foreach (var face in mesh.Faces)
        {
            var n = face.Normal();
            var loop = face.Vertices().Select(v => v.Position).ToList();
            for (int i = 1; i < loop.Count - 1; i++)
            {
                AppendVertex(positions, normals, loop[0], n);
                AppendVertex(positions, normals, loop[i], n);
                AppendVertex(positions, normals, loop[i + 1], n);
                uint baseIndex = (uint)(positions.Count / 3 - 3);
                indices.Add(baseIndex);
                indices.Add(baseIndex + 1);
                indices.Add(baseIndex + 2);
            }
        }

        return new RenderMesh
        {
            Positions = [.. positions],
            Normals = [.. normals],
            Indices = [.. indices],
        };
    }

    public static RenderMesh CreateSmooth(HalfEdgeMesh mesh)
    {
        var vertexNormals = mesh.ComputeVertexNormals();
        var positions = new float[mesh.VertexCount * 3];
        var normals = new float[mesh.VertexCount * 3];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var p = mesh.GetPosition(v);
            positions[v * 3] = (float)p.X;
            positions[v * 3 + 1] = (float)p.Y;
            positions[v * 3 + 2] = (float)p.Z;
            var n = vertexNormals[v];
            normals[v * 3] = (float)n.X;
            normals[v * 3 + 1] = (float)n.Y;
            normals[v * 3 + 2] = (float)n.Z;
        }

        var indices = new List<uint>();
        foreach (var face in mesh.Faces)
        {
            var loop = face.Vertices().Select(v => v.Index).ToList();
            for (int i = 1; i < loop.Count - 1; i++)
            {
                indices.Add((uint)loop[0]);
                indices.Add((uint)loop[i]);
                indices.Add((uint)loop[i + 1]);
            }
        }

        return new RenderMesh
        {
            Positions = positions,
            Normals = normals,
            Indices = [.. indices],
        };
    }

    private static void AppendVertex(List<float> positions, List<float> normals, in Vector3d p, in Vector3d n)
    {
        positions.Add((float)p.X);
        positions.Add((float)p.Y);
        positions.Add((float)p.Z);
        normals.Add((float)n.X);
        normals.Add((float)n.Y);
        normals.Add((float)n.Z);
    }
}
