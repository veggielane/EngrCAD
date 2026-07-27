using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The per-part / per-tab preparation entry points that let a host mesh on demand
/// (<see cref="Part.HasMesh"/>, <see cref="Part.Prepare"/>, <see cref="Tab.PreMesh"/>) —
/// the document-model half of the viewer's lazy tab meshing.
/// </summary>
public class OnDemandMeshingTests
{
    [Fact]
    public void HasMesh_IsFalseUntilThePartIsPrepared()
    {
        var part = new Part("block", Shape.Box(2, 1, 1));
        Assert.False(part.HasMesh);

        part.Prepare();
        Assert.True(part.HasMesh);

        // Prepare produces everything the display path needs, so nothing is left to
        // compute on a render thread.
        Assert.True(part.GetMesh().IsClosed);
        Assert.NotEmpty(part.GetFeatureEdges());
    }

    [Fact]
    public void Prepare_IsIdempotentAndKeepsTheFirstQuality()
    {
        var part = new Part("cyl", Shape.Cylinder(1, 2));
        part.Prepare(new MeshQuality { SegmentsPerCircle = 8 });
        int faces = part.GetMesh().FaceCount;

        part.Prepare(new MeshQuality { SegmentsPerCircle = 64 });
        Assert.Equal(faces, part.GetMesh().FaceCount);   // cached, not re-meshed
    }

    [Fact]
    public void Prepare_ReportsProgressAndReachesOne()
    {
        var fractions = new List<double>();
        var part = new Part("blob", Sdf.Sphere(1));
        part.Prepare(new MeshQuality { SdfResolution = 24 }, new ProgressCancel(fractions.Add));

        Assert.NotEmpty(fractions);
        Assert.Equal(1, fractions[^1], 9);
        Assert.All(fractions, f => Assert.InRange(f, 0, 1));
    }

    [Fact]
    public void Prepare_CancelsTheSdfRouteWithoutCachingAMesh()
    {
        // Surface Nets polls the ProgressCancel, so an SDF part can be abandoned
        // mid-flight — and abandoning it must leave NO cached mesh behind.
        var part = new Part("blob", Sdf.Sphere(1));
        var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => part.Prepare(new MeshQuality { SdfResolution = 32 }, new ProgressCancel(cancel.Token)));
        Assert.False(part.HasMesh);

        // ... and the part is still perfectly usable afterwards.
        part.Prepare(new MeshQuality { SdfResolution = 24 });
        Assert.True(part.HasMesh);
    }

    [Fact]
    public void TabPreMesh_PreparesOnlyThatTabsParts()
    {
        var scene = new Scene();
        var shown = scene.AddTab("shown");
        var hidden = scene.AddTab("hidden");
        var a = shown.Add(new Part("a", Shape.Box(1, 1, 1)));
        var b = shown.Add(new Part("b", Shape.Cylinder(0.5, 1)));
        var c = hidden.Add(new Part("c", Shape.Box(1, 1, 1)));

        shown.PreMesh();

        Assert.True(a.HasMesh);
        Assert.True(b.HasMesh);
        Assert.False(c.HasMesh);   // the tab nobody opened costs nothing
    }

    [Fact]
    public void TabPreMesh_MeshesAnInstancedPartOnce()
    {
        var scene = new Scene();
        var tab = scene.AddTab("assembly");
        var bolt = new Part("bolt", Shape.Cylinder(0.2, 1));
        var assembly = new Assembly("stack");
        for (int i = 0; i < 4; i++)
            assembly.Add(bolt, Frame3d.FromXY((i, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
        tab.Add(assembly);

        var fractions = new List<double>();
        tab.PreMesh(progress: new ProgressCancel(fractions.Add));

        Assert.True(bolt.HasMesh);
        Assert.Equal(4, tab.Instances().Count);
        Assert.Equal(1, fractions[^1], 9);
    }

    [Fact]
    public void TabPreMesh_StopsInsideTheFirstPartButKeepsItsLowering()
    {
        var scene = new Scene();
        var tab = scene.AddTab("model");
        var first = tab.Add(new Part("first", Shape.Box(1, 1, 1)));
        var second = tab.Add(new Part("second", Shape.Box(1, 1, 1)));

        // Cancel as soon as the first part reports: the second must never start.
        bool cancel = false;
        Assert.Throws<OperationCanceledException>(
            () => tab.PreMesh(progress: new ProgressCancel(() => cancel, _ => cancel = true)));

        Assert.False(second.HasMesh);
        // The B-Rep route observes cancellation in the TESSELLATION, so the first part is
        // abandoned mid-flight and caches no mesh...
        Assert.False(first.HasMesh);
        // ... but the LOWERING it paid for is kept, which is the expensive half and the
        // one that cannot be abandoned (TryGetSolid caches it, so a half-finished lowering
        // would poison the cache). A revisit re-tessellates from here.
        Assert.NotNull(first.TryGetSolid());
    }

    [Fact]
    public void TabPreMesh_CancelsBetweenPartsWhenNothingReportsMidPart()
    {
        // Cancelling without ever letting a part start: the between-parts checkpoint alone
        // must stop the job, with no part touched at all.
        var scene = new Scene();
        var tab = scene.AddTab("model");
        var first = tab.Add(new Part("first", Shape.Box(1, 1, 1)));

        Assert.Throws<OperationCanceledException>(
            () => tab.PreMesh(progress: new ProgressCancel(() => true)));

        Assert.False(first.HasMesh);
    }

    [Fact]
    public void ScenePreMesh_StillPreparesEverything()
    {
        // The eager path is unchanged: it is the whole-document sibling of Tab.PreMesh.
        var scene = new Scene();
        var one = scene.AddTab("one").Add(new Part("a", Shape.Box(1, 1, 1)));
        var two = scene.AddTab("two").Add(new Part("b", Shape.Box(1, 1, 1)));

        scene.PreMesh();

        Assert.True(one.HasMesh);
        Assert.True(two.HasMesh);
    }
}
