using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class TransformedMeshTests
{
    [Fact]
    public void Transformed_RigidMapPreservesVolumeAndTopology()
    {
        var box = MeshPrimitives.Box(2, 1.5, 1);
        var moved = box.Transformed(
            Matrix4d.CreateTranslation((3, -1, 2)) * Matrix4d.CreateRotationZ(0.7));
        moved.Validate();
        Assert.True(moved.IsClosed);
        Assert.Equal(box.VertexCount, moved.VertexCount);
        Assert.Equal(box.FaceCount, moved.FaceCount);
        Assert.Equal(box.Volume(), moved.Volume(), 12);
    }

    [Fact]
    public void Transformed_MirrorReversesWindingSoVolumeStaysPositive()
    {
        var box = MeshPrimitives.Box(new Aabb((0, 0, 0), (2, 1, 1)));
        var mirror = Matrix4d.CreateScale(new Vector3d(-1, 1, 1)); // reflect across x = 0
        Assert.True(mirror.Determinant < 0);

        var mirrored = box.Transformed(mirror);
        mirrored.Validate();
        Assert.True(mirrored.IsClosed);
        Assert.Equal(2.0, mirrored.Volume(), 12); // positive: winding was flipped

        // Positions are reflected: the box now occupies x ∈ [−2, 0].
        var (positions, _) = mirrored.ToIndexed();
        Assert.All(positions, p => Assert.True(p.X <= 1e-12 && p.X >= -2 - 1e-12));
    }

    [Fact]
    public void Transformed_MirrorOfMirror_RestoresTheOriginal()
    {
        var mesh = MeshPrimitives.Cylinder(1, 2, 16);
        var mirror = Matrix4d.CreateScale(new Vector3d(1, -1, 1));
        var back = mesh.Transformed(mirror).Transformed(mirror);
        back.Validate();
        Assert.True(back.IsClosed);
        Assert.Equal(mesh.Volume(), back.Volume(), 12);

        var original = mesh.ToIndexed().Positions;
        var restored = back.ToIndexed().Positions;
        Assert.Equal(original.Length, restored.Length);
        for (int i = 0; i < original.Length; i++)
            Assert.True(original[i].AreEqual(restored[i], Tolerance.Default));
    }
}
