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

    /// <summary>
    /// The source <see cref="HalfEdgeMesh"/> vertex each render vertex came from — one
    /// entry per render vertex, and the identity for <see cref="CreateSmooth"/>.
    /// <para>The flat variant repeats a position once per incident triangle, so a
    /// consumer holding per-SOURCE-vertex data (baked occlusion, a simulation
    /// <see cref="MeshField"/>) needs this to spread it across the duplicates. Recovering
    /// it by hashing positions works, and is what the occlusion bake does, but it is
    /// ambiguous wherever two distinct source vertices share a position — recording the
    /// index while it is known costs one int per vertex and cannot be wrong.</para>
    /// </summary>
    public int[] SourceVertices { get; init; } = [];

    /// <summary>
    /// The source <see cref="HalfEdgeMesh"/> FACE each render vertex came from — one
    /// entry per render vertex, <see cref="SourceVertices"/>' sibling, and what places a
    /// CELL-associated <see cref="MeshField"/> on the flat mesh (every duplicate of a
    /// face's corners takes that face's value, so the cell renders flat with no shader
    /// change). Populated by <see cref="CreateFlat"/> only: a SMOOTH mesh shares vertices
    /// between faces, so "which face's value does this vertex take" has no answer there,
    /// and an empty map is the honest statement rather than an arbitrary pick.
    /// </summary>
    public int[] SourceFaces { get; init; } = [];

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;

    public static RenderMesh CreateFlat(HalfEdgeMesh mesh)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var indices = new List<uint>();
        var sources = new List<int>();
        var sourceFaces = new List<int>();

        foreach (var face in mesh.Faces)
        {
            var n = face.Normal();
            var loop = face.Vertices().ToList();
            // The shared fan rule: a quad splits along its shorter 3D diagonal. On a
            // non-planar cell the two splits are different SURFACES, so this has to agree
            // with what SignedVolume measures and what the writers export. Source indices
            // follow the same corners, so a field lookup still names the vertex it drew.
            var corners = new Vector3d[loop.Count];
            for (int i = 0; i < loop.Count; i++)
                corners[i] = loop[i].Position;
            int apex = PolygonFan.Apex(corners);
            for (int i = 1; i < loop.Count - 1; i++)
            {
                int b = PolygonFan.Corner(apex, loop.Count, i);
                int c = PolygonFan.Corner(apex, loop.Count, i + 1);
                AppendVertex(positions, normals, loop[apex].Position, n);
                AppendVertex(positions, normals, loop[b].Position, n);
                AppendVertex(positions, normals, loop[c].Position, n);
                sources.Add(loop[apex].Index);
                sources.Add(loop[b].Index);
                sources.Add(loop[c].Index);
                sourceFaces.Add(face.Index);
                sourceFaces.Add(face.Index);
                sourceFaces.Add(face.Index);
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
            SourceVertices = [.. sources],
            SourceFaces = [.. sourceFaces],
        };
    }

    public static RenderMesh CreateSmooth(HalfEdgeMesh mesh)
    {
        var vertexNormals = mesh.ComputeVertexNormals();
        var positions = new float[mesh.VertexCount * 3];
        var normals = new float[mesh.VertexCount * 3];
        var sources = new int[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            sources[v] = v;
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
            int apex = PolygonFan.Apex([.. loop.Select(mesh.GetPosition)]);
            for (int i = 1; i < loop.Count - 1; i++)
            {
                indices.Add((uint)loop[apex]);
                indices.Add((uint)loop[PolygonFan.Corner(apex, loop.Count, i)]);
                indices.Add((uint)loop[PolygonFan.Corner(apex, loop.Count, i + 1)]);
            }
        }

        return new RenderMesh
        {
            Positions = positions,
            Normals = normals,
            Indices = [.. indices],
            SourceVertices = sources,
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
