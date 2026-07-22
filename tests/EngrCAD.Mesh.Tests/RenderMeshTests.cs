using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class RenderMeshTests
{
    [Fact]
    public void CreateFlat_BoxHasPerFaceVertices()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var render = RenderMesh.CreateFlat(box);

        // 6 quads → 12 triangles, flat shading duplicates 3 vertices per triangle.
        Assert.Equal(12, render.TriangleCount);
        Assert.Equal(36, render.VertexCount);
        Assert.Equal(render.Positions.Length, render.Normals.Length);

        // Every normal is a unit axis vector.
        for (int v = 0; v < render.VertexCount; v++)
        {
            float x = Math.Abs(render.Normals[v * 3]);
            float y = Math.Abs(render.Normals[v * 3 + 1]);
            float z = Math.Abs(render.Normals[v * 3 + 2]);
            Assert.Equal(1.0f, x + y + z, 5);
            Assert.Equal(1.0f, Math.Max(x, Math.Max(y, z)), 5);
        }
    }

    [Fact]
    public void CreateSmooth_SharesVertices()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        var render = RenderMesh.CreateSmooth(box);

        Assert.Equal(8, render.VertexCount);
        Assert.Equal(12, render.TriangleCount);
        Assert.All(render.Indices, i => Assert.InRange(i, 0u, 7u));
    }

    [Fact]
    public void ObjWriter_EmitsVerticesAndFaces()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (1, 1, 1)));
        using var writer = new StringWriter();
        ObjWriter.Write(box, writer);
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(8, lines.Count(l => l.StartsWith("v ")));
        Assert.Equal(6, lines.Count(l => l.StartsWith("f ")));

        // Faces reference 1-based vertex indices within range.
        foreach (var line in lines.Where(l => l.StartsWith("f ")))
        {
            var indices = line[2..].Split(' ').Select(int.Parse).ToList();
            Assert.Equal(4, indices.Count);
            Assert.All(indices, i => Assert.InRange(i, 1, 8));
        }
    }
}
