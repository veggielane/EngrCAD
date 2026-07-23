using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

public class SceneTests
{
    [Fact]
    public void Part_CarriesItsOwnInfo_MeshOnDemand()
    {
        var mesh = MeshPrimitives.Box(1, 1, 1);
        var part = new Part("box", mesh);

        Assert.Equal("box", part.Name);
        Assert.Same(mesh, part.Geometry);
        Assert.Same(mesh, part.GetMesh());
        Assert.Null(part.Color);
        Assert.Equal(Matrix4d.Identity, part.Transform);
    }

    [Fact]
    public void Part_TessellatesBrepAndPolygonizesSdf()
    {
        var brepPart = new Part("block", SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 1, 1))));
        var brepMesh = brepPart.GetMesh();
        Assert.True(brepMesh.IsClosed);
        Assert.True(Math.Abs(brepMesh.Volume() - 2.0) < 1e-9);
        Assert.Same(brepMesh, brepPart.GetMesh()); // cached

        var sdfPart = new Part("ball", Sdf.Sphere(1));
        var sdfMesh = sdfPart.GetMesh(new MeshQuality { SdfResolution = 32 });
        Assert.True(sdfMesh.IsClosed);
        double exact = 4.0 / 3.0 * Math.PI;
        Assert.True(Math.Abs(sdfMesh.Volume() - exact) / exact < 0.05);
    }

    [Fact]
    public void SceneAdd_UsesDefaultTabAndCyclesPalette()
    {
        var scene = new Scene();
        var a = scene.Add(new Part("a", MeshPrimitives.Box(1, 1, 1)));
        var b = scene.Add(new Part("b", MeshPrimitives.Box(1, 1, 1)));

        var tab = Assert.Single(scene.Tabs);
        Assert.Equal("Model", tab.Name);
        Assert.Equal(2, tab.Parts.Count);
        Assert.NotNull(a.Color);
        Assert.NotEqual(a.Color, b.Color);
    }

    [Fact]
    public void Tabs_HoldTheirOwnParts_NamesUniquePerTab()
    {
        var scene = new Scene();
        var body = scene.AddTab("body");
        var lid = scene.AddTab("lid");
        body.Add(new Part("main", MeshPrimitives.Box(1, 1, 1)));
        lid.Add(new Part("main", MeshPrimitives.Box(1, 1, 0.2))); // same name, other tab: fine

        Assert.Equal(2, scene.Tabs.Count);
        Assert.Equal(2, scene.AllParts.Count());
        Assert.Throws<ArgumentException>(() => body.Add(new Part("main", MeshPrimitives.Box(1, 1, 1))));
        Assert.Throws<ArgumentException>(() => scene.AddTab("body"));
    }

    [Fact]
    public void TabBounds_AppliesPartTransforms()
    {
        var scene = new Scene();
        var tab = scene.AddTab("t");
        tab.Add(new Part("a", MeshPrimitives.Box(1, 1, 1),
            transform: Matrix4d.CreateTranslation((5, 0, 0))));
        tab.Add(new Part("b", MeshPrimitives.Box(1, 1, 1),
            transform: Matrix4d.CreateTranslation((-5, 0, 0))));

        var bounds = tab.Bounds();
        Assert.True(bounds.Min.X < -4.9);
        Assert.True(bounds.Max.X > 4.9);
    }

    [Fact]
    public void Part_AcceptsShape_ExplicitColorAndTransformKept()
    {
        var move = Matrix4d.CreateTranslation((3, 0, 0));
        var part = new Part("drilled", Shape.Box(1, 1, 1) - Shape.Cylinder(0.3, 2), Palette.Brass, move);

        Assert.Equal(Palette.Brass, part.Color);
        Assert.Equal(move, part.Transform);
        var mesh = part.GetMesh();
        Assert.True(mesh.IsClosed);
        double exact = 1 - Math.PI * 0.09;
        Assert.True(Math.Abs(mesh.Volume() - exact) / exact < 0.01); // B-Rep route: crisp
    }

    [Fact]
    public void Part_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() => new Part("", MeshPrimitives.Box(1, 1, 1)));
    }
}
