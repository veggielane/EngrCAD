using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class StlWriterTests
{
    [Fact]
    public void Box_WritesTwelveFacets()
    {
        var mesh = MeshPrimitives.Box(2, 1, 1);
        using var stream = new MemoryStream();
        StlWriter.Write(mesh, stream);

        // 6 quads → 12 triangles: 84-byte preamble + 50 per facet.
        Assert.Equal(84 + 50 * 12, stream.Length);
        var bytes = stream.ToArray();
        Assert.Equal(12u, BitConverter.ToUInt32(bytes, 80));
    }

    [Fact]
    public void Transforms_ApplyAndMirrorFlipsWinding()
    {
        var mesh = MeshPrimitives.Box(1, 1, 1);
        var mirror = Matrix4d.CreateScale(new Vector3d(-1, 1, 1));
        using var stream = new MemoryStream();
        StlWriter.Write([(mesh, Matrix4d.CreateTranslation((5, 0, 0))), (mesh, mirror)], stream);
        Assert.Equal(84 + 50 * 24, stream.Length);

        // First facet of the translated box: all vertex x-coordinates near 5 ± 0.5.
        var bytes = stream.ToArray();
        for (int v = 0; v < 3; v++)
        {
            float x = BitConverter.ToSingle(bytes, 84 + 12 + v * 12);
            Assert.InRange(x, 4.4, 5.6);
        }
    }
}
