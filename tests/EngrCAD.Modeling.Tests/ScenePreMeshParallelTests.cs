using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Scene.PreMesh"/> primes every distinct part in parallel. Parts are
/// independent by construction, so the contract is that nothing about the OUTPUT may
/// depend on scheduling — and that a part which fails to lower still reports the same
/// exception the sequential pass reported, not a scheduling-dependent aggregate.
/// </summary>
public class ScenePreMeshParallelTests
{
    /// <summary>A scene with a bit of everything (B-Rep, Shape, SDF, mesh) plus one
    /// Shape instance shared by two parts and one part instanced through an assembly.</summary>
    private static (Scene Scene, Shape Shared) MixedScene()
    {
        var shared = Shape.Box(2, 1.4, 0.8) - Shape.Cylinder(0.3, 3).Translate(0.55, 0, 0);

        var scene = new Scene(new MeshQuality { SegmentsPerCircle = 16, SdfResolution = 24 });
        var tab = scene.AddTab("mixed");
        tab.Add(new Part("shared a", shared));
        tab.Add(new Part("shared b", shared, transform: Matrix4d.CreateTranslation((5, 0, 0))));
        tab.Add(new Part("brep", SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 1, 1)))));
        tab.Add(new Part("sdf", Sdf.Sphere(1).SmoothUnion(Sdf.Sphere(0.8).Translate((1, 0, 0)), 0.3)));
        tab.Add(new Part("mesh", MeshPrimitives.UvSphere(1, 24, 12)));
        tab.Add(new Part("sketched", Shape.Extrude(Sketch.RoundedRectangle(3, 2, 0.4), 0.5)));
        tab.Add(new Part("revolved", Shape.Revolve(
            Sketch.Start(0, 0).LineTo(1, 0).LineTo(0.7, 2).LineTo(0, 2).Close())));

        var bolt = new Part("bolt", Shape.Cylinder(0.16, 0.9) | Shape.Cylinder(0.34, 0.26).Translate(0, 0, 0.58));
        var assembly = new Assembly("stack");
        for (int i = 0; i < 4; i++)
            assembly.Add(bolt, Frame3d.FromXY((i, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
        tab.Add(assembly);

        return (scene, shared);
    }

    private static (int Vertices, int Faces, double Volume)[] Fingerprint(Scene scene) =>
        [.. scene.AllParts.Select(p =>
        {
            var mesh = p.GetMesh();
            return (mesh.VertexCount, mesh.FaceCount, mesh.Volume());
        })];

    [Fact]
    public void ParallelPreMesh_ProducesBitIdenticalMeshesToSequentialPriming()
    {
        var quality = new MeshQuality { SegmentsPerCircle = 16, SdfResolution = 24 };

        // Sequential reference: prime each part by hand, in scene order, on this thread.
        var (sequential, _) = MixedScene();
        foreach (var part in sequential.AllParts)
        {
            part.GetMesh(quality);
            part.GetFeatureEdges(quality);
        }

        var (parallel, _) = MixedScene();
        parallel.PreMesh();

        var expected = Fingerprint(sequential);
        var actual = Fingerprint(parallel);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Vertices, actual[i].Vertices);
            Assert.Equal(expected[i].Faces, actual[i].Faces);
            // Bit-for-bit: the same geometry through the same code, only on another thread.
            Assert.Equal(expected[i].Volume, actual[i].Volume);
        }

        // Feature edges were primed too — off the render thread is the whole point.
        // (Smoothly blended SDFs and UV spheres legitimately have no sharp edges, so
        // this checks a body that certainly does.)
        var brep = parallel.AllParts.First(p => p.Name == "brep");
        Assert.Equal(
            sequential.AllParts.First(p => p.Name == "brep").GetFeatureEdges(quality).Count,
            brep.GetFeatureEdges(quality).Count);
        Assert.NotEmpty(brep.GetFeatureEdges(quality));
    }

    [Fact]
    public void SharedShapeAndInstancedPart_AreEachMeshedExactlyOnce()
    {
        var (scene, _) = MixedScene();
        scene.PreMesh();

        // AllParts dedupes by reference: the four bolt occurrences are one Part.
        Assert.Equal(8, scene.AllParts.Count());
        Assert.Equal(4 + 8 - 1, scene.AllInstances.Count()); // 7 loose parts + 4 bolt placements

        // Two parts sharing ONE Shape instance both get their own (equal) mesh: lowering
        // builds fresh geometry, so concurrent lowering of the same graph cannot collide.
        var a = scene.AllParts.First(p => p.Name == "shared a");
        var b = scene.AllParts.First(p => p.Name == "shared b");
        Assert.NotSame(a.GetMesh(), b.GetMesh());
        Assert.Equal(a.GetMesh().Volume(), b.GetMesh().Volume(), 12);
    }

    [Fact]
    public void PartThatCannotLower_RethrowsTheOriginalException_NotAnAggregate()
    {
        // A bore that swallows a rounded corner and breaks out through both adjacent walls:
        // a cut chain crossing a face boundary part-way, which the v1 exact boolean refuses.
        // Sequentially PreMesh surfaced that exception directly; running in parallel must not
        // wrap it in an AggregateException.
        var unclosable = BooleanFailureTests.UnclosableBreakout();

        var scene = new Scene();
        scene.Add(new Part("fine", MeshPrimitives.Box(1, 1, 1)));
        scene.Add(new Part("broken", unclosable));
        scene.Add(new Part("also fine", SolidFactory.MakeBox(new Aabb((0, 0, 0), (1, 1, 1)))));

        var error = Assert.Throws<InvalidOperationException>(() => scene.PreMesh());
        Assert.Contains("unclosed solid", error.Message);

        // The healthy parts were still primed — priming is per-part and independent.
        Assert.NotNull(scene.AllParts.First(p => p.Name == "also fine").GetMesh());
    }
}
