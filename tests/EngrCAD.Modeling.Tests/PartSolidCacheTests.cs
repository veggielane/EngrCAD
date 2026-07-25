using System.Diagnostics;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// One lowering per part: the display mesh, the feature-edge overlay, selector
/// annotations, STEP export, and construction previews all share the solid
/// <see cref="Part.TryGetSolid"/> caches. Before this, a Shape part compiled its B-Rep
/// once per consumer.
/// </summary>
public class PartSolidCacheTests
{
    [Fact]
    public void SolidIsLoweredOnceAndCached()
    {
        var part = new Part("body", Shape.Box(4, 3, 2) - Shape.Cylinder(0.5, 5));
        var first = part.TryGetSolid();
        Assert.NotNull(first);
        Assert.Same(first, part.TryGetSolid());
    }

    [Fact]
    public void BrepPartsHandBackTheirOwnSolid()
    {
        var solid = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 2)));
        Assert.Same(solid, new Part("box", solid).TryGetSolid());
    }

    [Fact]
    public void PartsWithoutAnExactSolidReportNull()
    {
        Assert.Null(new Part("mesh", MeshPrimitives.Box(1, 1, 1)).TryGetSolid());
        Assert.Null(new Part("sdf", Sdf.Sphere(1)).TryGetSolid());
        // A smooth blend has no B-Rep form at all.
        Assert.Null(new Part("blend",
            Shape.Box(2, 2, 2).SmoothUnion(Shape.Sphere(1.2), 0.4)).TryGetSolid());
    }

    [Fact]
    public void TheDisplayMeshIsTheCachedSolidTessellated()
    {
        var quality = new MeshQuality { SegmentsPerCircle = 24, CurveSamples = 12 };
        var shape = Shape.Box(4, 3, 2) - Shape.Cylinder(0.5, 5);
        var part = new Part("body", shape);

        var mesh = part.GetMesh(quality);
        var direct = BRepTessellator.Tessellate(
            part.TryGetSolid()!, quality.SegmentsPerCircle, quality.CurveSamples);
        Assert.Equal(direct.FaceCount, mesh.FaceCount);
        Assert.Equal(direct.Volume(), mesh.Volume(), 9);
    }

    [Fact]
    public void SdfOnlyPartsStillMeshThroughTheImplicitRoute()
    {
        var part = new Part("blend", Shape.Box(2, 2, 2).SmoothUnion(Shape.Sphere(1.2), 0.4));
        var mesh = part.GetMesh(new MeshQuality { SdfResolution = 32 });
        Assert.True(mesh.IsClosed);
        Assert.True(mesh.FaceCount > 0);
    }

    [Fact]
    public void FeatureEdgesReuseTheSolidInsteadOfLoweringAgain()
    {
        // A pattern of bosses: lowering costs hundreds of ms, edge extraction over an
        // existing solid a couple. Without the shared cache the second call repeats the
        // whole compile, so the ratio is ~1; with it, it is a small fraction. The bound
        // is deliberately loose (a quarter) so this measures the missing lowering, not
        // machine speed.
        var quality = new MeshQuality { SegmentsPerCircle = 24, CurveSamples = 12 };
        var part = new Part("pattern",
            Shape.Cylinder(20, 4) | Shape.Cylinder(3, 10).Translate(15, 0, 4)
                .PatternCircular(6, Vector3d.Zero, Vector3d.UnitZ));

        var watch = Stopwatch.StartNew();
        part.GetMesh(quality);
        var lowering = watch.Elapsed;
        watch.Restart();
        var edges = part.GetFeatureEdges(quality);
        var extraction = watch.Elapsed;

        Assert.NotEmpty(edges);
        Assert.True(extraction < lowering / 4,
            $"feature edges took {extraction.TotalMilliseconds:F0} ms against a "
            + $"{lowering.TotalMilliseconds:F0} ms lowering - the solid was not reused");
    }

    [Fact]
    public void SelectorAnnotationsResolveAgainstTheSharedSolid()
    {
        var part = new Part("plate", Shape.Box(10, 6, 4));
        part.Annotate(LinearDimension.BetweenFaces(
            s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First(),
            s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First()));

        Assert.True(part.TryResolveAnnotations(out var resolved, out string? error));
        Assert.Null(error);
        var dimension = Assert.Single(resolved);
        Assert.Equal(4, dimension.AnchorA.DistanceTo(dimension.AnchorB), 9);
        // The solid the selectors ran against is the part's one cached solid.
        Assert.NotNull(part.TryGetSolid());
    }

    [Fact]
    public void AnnotationsOnNonSolidPartsStillReportInsteadOfThrowing()
    {
        var part = new Part("blend", Shape.Box(2, 2, 2).SmoothUnion(Shape.Sphere(1.2), 0.4));
        part.Annotate(LinearDimension.BetweenFaces(
            s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First(),
            s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First()));

        Assert.False(part.TryResolveAnnotations(out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("B-Rep-representable", error);
    }

    [Fact]
    public void PreMeshPrimesTheSolidForEveryPart()
    {
        var scene = new Scene(new MeshQuality { SegmentsPerCircle = 16, CurveSamples = 8 });
        var shaped = scene.Add(new Part("shape", Shape.Box(4, 3, 2) - Shape.Cylinder(0.5, 5)));
        var meshed = scene.Add(new Part("mesh", MeshPrimitives.Box(1, 1, 1)));
        scene.PreMesh();

        Assert.NotNull(shaped.TryGetSolid());
        Assert.Null(meshed.TryGetSolid());
    }

    [Fact]
    public void ConstructionPreviewsCanReuseAnAlreadyLoweredSolid()
    {
        var quality = new MeshQuality { SegmentsPerCircle = 24, CurveSamples = 12 };
        var part = new Part("body", Shape.Box(4, 3, 2) - Shape.Cylinder(0.5, 5));
        var root = part.ConstructionTree()!;

        var withSolid = ConstructionPreview.Build(root, quality, part.TryGetSolid());
        var withoutSolid = ConstructionPreview.Build(root, quality);
        Assert.Null(withSolid.Error);
        Assert.Equal(withoutSolid.Segments.Count, withSolid.Segments.Count);
    }
}
