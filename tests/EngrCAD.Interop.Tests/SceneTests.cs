using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Interop.Tests;

public class SceneTests
{
    [Fact]
    public void Add_Mesh_KeepsGeometryAsIs()
    {
        var scene = new Scene();
        var mesh = MeshPrimitives.Box(1, 1, 1);
        var part = scene.Add("box", mesh);

        Assert.Same(mesh, part.Mesh);
        Assert.Same(mesh, part.Source);
        Assert.Equal("box", part.Name);
        Assert.Single(scene.Parts);
    }

    [Fact]
    public void Add_BrepSolid_TessellatesToClosedMesh()
    {
        var scene = new Scene();
        var solid = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 1, 1)));
        var part = scene.Add("block", solid);

        part.Mesh.Validate();
        Assert.True(part.Mesh.IsClosed);
        Assert.True(Math.Abs(part.Mesh.Volume() - 2.0) < 1e-9);
        Assert.Same(solid, part.Source);
    }

    [Fact]
    public void Add_Sdf_PolygonizesToClosedMesh()
    {
        var scene = new Scene(new SceneOptions { SdfResolution = 32 });
        var part = scene.Add("ball", Sdf.Sphere(1));

        part.Mesh.Validate();
        Assert.True(part.Mesh.IsClosed);
        double exact = 4.0 / 3.0 * Math.PI;
        Assert.True(Math.Abs(part.Mesh.Volume() - exact) / exact < 0.05);
    }

    [Fact]
    public void Add_DefaultsCycleThroughPalette()
    {
        var scene = new Scene();
        var a = scene.Add("a", MeshPrimitives.Box(1, 1, 1));
        var b = scene.Add("b", MeshPrimitives.Box(1, 1, 1));

        Assert.NotEqual(a.Color, b.Color);
        Assert.Equal(Matrix4d.Identity, a.Transform);
    }

    [Fact]
    public void Add_ExplicitColorAndTransformAreKept()
    {
        var scene = new Scene();
        var move = Matrix4d.CreateTranslation((3, 0, 0));
        var part = scene.Add("box", MeshPrimitives.Box(1, 1, 1), Palette.Brass, move);

        Assert.Equal(Palette.Brass, part.Color);
        Assert.Equal(move, part.Transform);
    }

    [Fact]
    public void Add_RejectsEmptyAndDuplicateNames()
    {
        var scene = new Scene();
        scene.Add("box", MeshPrimitives.Box(1, 1, 1));

        Assert.Throws<ArgumentException>(() => scene.Add("", MeshPrimitives.Box(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => scene.Add("box", MeshPrimitives.Box(1, 1, 1)));
    }

    [Fact]
    public void Bounds_AppliesPartTransforms()
    {
        var scene = new Scene();
        scene.Add("a", MeshPrimitives.Box(1, 1, 1), transform: Matrix4d.CreateTranslation((5, 0, 0)));
        scene.Add("b", MeshPrimitives.Box(1, 1, 1), transform: Matrix4d.CreateTranslation((-5, 0, 0)));

        var bounds = scene.Bounds();
        Assert.True(bounds.Min.X < -4.9);
        Assert.True(bounds.Max.X > 4.9);
        Assert.True(bounds.Max.X - bounds.Min.X > 9);
    }

    [Fact]
    public void Bounds_EmptySceneIsEmpty()
    {
        Assert.True(new Scene().Bounds().IsEmpty);
    }
}
