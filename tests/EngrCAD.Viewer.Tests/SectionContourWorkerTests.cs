using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The streaming half of the section-isoline rebuild: SectionContourWorker runs
/// extraction on a background task (no GL, so no offscreen-gl collection needed) and
/// the render thread adopts results only when they are still current. These tests
/// drive the state machine directly — kick, wait for the ready signal, adopt — and
/// pin the two invalidation rules a stale result must never survive: an Invalidate
/// between kick and adopt, and a newer kick superseding an older one.
/// </summary>
public class SectionContourWorkerTests
{
    private static (List<PartInstance> Instances, bool[] Visible) SphereScene()
    {
        var part = new Part("sphere", Sdf.Sphere(5));
        return ([new PartInstance(part, Matrix4d.Identity, part.Name)], [true]);
    }

    private static SectionContourBuild BuildAndWait(
        SectionContourWorker worker, IReadOnlyList<PartInstance> instances,
        bool[] visible, IReadOnlyList<SectionPlane> planes)
    {
        using var ready = new ManualResetEventSlim();
        worker.EnsureBuilding(instances, visible, planes, ready.Set);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "background build never signalled");
        var build = worker.TryAdopt();
        Assert.NotNull(build);
        return build;
    }

    [Fact]
    public void EnsureBuilding_BuildsOffThreadAndAdoptsOnce()
    {
        var (instances, visible) = SphereScene();
        var worker = new SectionContourWorker();
        var planes = new List<SectionPlane> { SectionPlane.On(SectionAxis.Z, 0) };

        var build = BuildAndWait(worker, instances, visible, planes);
        Assert.Single(build.Geometries);
        Assert.Equal(1, build.Geometries[0].PartCount);
        Assert.True(build.Geometries[0].ZeroVertices.Length > 0);
        Assert.Empty(build.Failures);
        Assert.Equal(planes, build.Planes);

        // Adoption is one-shot: the result is handed over exactly once.
        Assert.Null(worker.TryAdopt());
        Assert.False(worker.Building);
    }

    [Fact]
    public void EnsureBuilding_SameTargetWhileInFlight_DoesNotKickASecondBuild()
    {
        var (instances, visible) = SphereScene();
        var worker = new SectionContourWorker();
        var planes = new List<SectionPlane> { SectionPlane.On(SectionAxis.Z, 0) };

        int signals = 0;
        using var ready = new ManualResetEventSlim();
        void Signal() { Interlocked.Increment(ref signals); ready.Set(); }

        worker.EnsureBuilding(instances, visible, planes, Signal);
        // Re-requesting the identical target (every frame does this while stale) must
        // not start a second task; the visibility list is snapshotted, so pass a copy
        // to prove comparison is by value.
        worker.EnsureBuilding(instances, [.. visible], [.. planes], Signal);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
        Assert.NotNull(worker.TryAdopt());
        Assert.Equal(1, signals);
    }

    [Fact]
    public void Invalidate_BetweenKickAndAdopt_DiscardsTheResult()
    {
        var (instances, visible) = SphereScene();
        var worker = new SectionContourWorker();
        var planes = new List<SectionPlane> { SectionPlane.On(SectionAxis.Z, 0) };

        using var ready = new ManualResetEventSlim();
        worker.EnsureBuilding(instances, visible, planes, ready.Set);
        worker.Invalidate();   // scene swapped while the build ran
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)));
        Assert.Null(worker.TryAdopt());   // stale result must never land
    }

    [Fact]
    public void NewerTarget_SupersedesAnOlderInFlightBuild()
    {
        var (instances, visible) = SphereScene();
        var worker = new SectionContourWorker();

        using var readyOld = new ManualResetEventSlim();
        using var readyNew = new ManualResetEventSlim();
        worker.EnsureBuilding(
            instances, visible, [SectionPlane.On(SectionAxis.Z, 0)], readyOld.Set);
        // The plane moved before the first build was adopted (a nudge drag): the new
        // target supersedes, and only ITS result may land.
        worker.EnsureBuilding(
            instances, visible, [SectionPlane.On(SectionAxis.Z, 1)], readyNew.Set);

        Assert.True(readyOld.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(readyNew.Wait(TimeSpan.FromSeconds(30)));

        // Whichever order the tasks finished, adoption returns exactly the newer
        // target's build (the older generation is rejected).
        SectionContourBuild? adopted = worker.TryAdopt() ?? worker.TryAdopt();
        Assert.NotNull(adopted);
        Assert.Equal(SectionPlane.On(SectionAxis.Z, 1), adopted.Planes.Single());
        Assert.Null(worker.TryAdopt());
    }
}
