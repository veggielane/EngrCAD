using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The on-demand tab meshing state machine (<see cref="TabMeshLoader"/>) — headless,
/// with a fake "prepare" so the tests drive timing and failures instead of waiting for
/// real tessellation. The UI itself is not unit-testable; this is where the rules that
/// usually break such a feature are pinned: request → publish → complete, staleness on
/// a tab switch, a part that throws, and the already-meshed fast path.
/// </summary>
public class TabMeshLoaderTests
{
    /// <summary>Collects the callbacks instead of a UI: <see cref="Drain"/> stands in
    /// for the dispatcher turn, so a test decides exactly when results are applied.</summary>
    private sealed class Harness
    {
        private readonly List<Action> _posted = [];

        public List<TabMeshBatch> Batches { get; } = [];
        public List<TabMeshProgress> Progress { get; } = [];
        public List<TabMeshCompletion> Completions { get; } = [];

        public TabMeshLoader Create(Action<Part, MeshQuality?, ProgressCancel?> prepare)
        {
            var loader = new TabMeshLoader(Post, prepare);
            loader.Ready = Batches.Add;
            loader.Progress = Progress.Add;
            loader.Completed = Completions.Add;
            return loader;
        }

        private void Post(Action callback)
        {
            lock (_posted)
                _posted.Add(callback);
        }

        /// <summary>Runs everything posted so far (the UI thread catching up).</summary>
        public void Drain()
        {
            while (true)
            {
                Action[] batch;
                lock (_posted)
                {
                    if (_posted.Count == 0)
                        return;
                    batch = [.. _posted];
                    _posted.Clear();
                }
                foreach (var callback in batch)
                    callback();
            }
        }
    }

    /// <summary>A part whose geometry is a mesh — cheap, and <c>Prepare</c> is never
    /// actually called in these tests (the fake stands in for it).</summary>
    private static Part MeshPart(string name) =>
        new(name, EngrCAD.Mesh.MeshPrimitives.Box(1, 1, 1));

    /// <summary>An unmeshed part: a Shape is only meshed on demand, so
    /// <c>Part.HasMesh</c> is false until something prepares it.</summary>
    private static Part LazyPart(string name) => new(name, Shape.Box(1, 1, 1));

    private static TabMeshRequest Request(string tab, params Part[] parts) =>
        new(tab, [.. parts.Select(p => new PartInstance(p, p.Transform, p.Name))], null, Frame: true);

    [Fact]
    public async Task PublishesEveryPartInOrderAndCompletesOnce()
    {
        var harness = new Harness();
        var prepared = new List<string>();
        var loader = harness.Create((part, _, progress) =>
        {
            prepared.Add(part.Name);
            progress?.Report(1);
        });

        var parts = new[] { LazyPart("a"), LazyPart("b"), LazyPart("c") };
        await loader.Start(Request("model", parts));
        harness.Drain();

        Assert.Equal(["a", "b", "c"], prepared);

        // Batches grow, never shrink, and always keep tab order — the host can hand
        // each one straight to the viewport.
        Assert.All(harness.Batches, batch => Assert.Equal("model", batch.TabName));
        var sizes = harness.Batches.Select(b => b.Ready.Count).ToList();
        Assert.Equal(sizes.OrderBy(n => n), sizes);
        var final = harness.Batches[^1];
        Assert.True(final.Final);
        Assert.Equal(["a", "b", "c"], final.Ready.Select(i => i.Part.Name));
        Assert.Empty(final.Failed);

        var completion = Assert.Single(harness.Completions);
        Assert.Equal(3, completion.PartCount);
        Assert.False(completion.Cancelled);
        Assert.Empty(completion.Failures);

        // The bar reaches the end, and every message names the tab and a real part.
        Assert.Equal(1, harness.Progress[^1].Fraction, 9);
        Assert.All(harness.Progress, p =>
        {
            Assert.Equal(3, p.Total);
            Assert.Contains(p.PartName, (string[])["a", "b", "c"]);
            Assert.False(string.IsNullOrWhiteSpace(p.Flavor));
        });
    }

    [Fact]
    public async Task AlreadyMeshedTabPublishesImmediatelyWithNoWork()
    {
        var harness = new Harness();
        bool prepared = false;
        var loader = harness.Create((_, _, _) => prepared = true);

        // Mesh-backed parts are meshed the moment they are asked for; prime them the
        // way a previous visit (or eager PreMesh) would have.
        var parts = new[] { MeshPart("a"), MeshPart("b") };
        foreach (var part in parts)
            part.GetMesh();

        var task = loader.Start(Request("model", parts));
        Assert.Same(Task.CompletedTask, task);
        await task;
        harness.Drain();

        Assert.False(prepared);
        var batch = Assert.Single(harness.Batches);
        Assert.True(batch.Final);
        Assert.Equal(2, batch.Ready.Count);
        // No work means no progress UI and no "meshed in ..." noise.
        Assert.Empty(harness.Progress);
        Assert.Empty(harness.Completions);
    }

    [Fact]
    public async Task ResumesFromTheAlreadyMeshedPrefix()
    {
        var harness = new Harness();
        var prepared = new List<string>();
        var loader = harness.Create((part, _, _) => prepared.Add(part.Name));

        var first = MeshPart("cached");
        first.GetMesh();
        var parts = new[] { first, LazyPart("fresh") };

        await loader.Start(Request("model", parts));
        harness.Drain();

        Assert.Equal(["fresh"], prepared);                 // the cached part is not redone
        Assert.Equal(1, harness.Progress[0].Total);        // ... nor counted as work
        Assert.Equal(2, harness.Batches[^1].Ready.Count);  // ... but it IS displayed
    }

    [Fact]
    public async Task ASupersededJobNeitherFinishesNorLandsInTheNewTab()
    {
        var harness = new Harness();
        using var slowPartStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var prepared = new List<string>();

        var loader = harness.Create((part, _, _) =>
        {
            lock (prepared)
                prepared.Add(part.Name);
            if (part.Name != "slow")
                return;
            slowPartStarted.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });

        var slowTab = Request("slow-tab", LazyPart("slow"), LazyPart("never"));
        var task = loader.Start(slowTab);
        Assert.True(slowPartStarted.Wait(TimeSpan.FromSeconds(30)));

        // The user switches tabs while the first is still meshing.
        var quickTab = Request("quick-tab", LazyPart("quick"));
        var second = loader.Start(quickTab);
        release.Set();
        await Task.WhenAll(task, second);
        harness.Drain();

        // The abandoned job stopped at the next part boundary — "never" was never
        // touched — and nothing it produced was applied.
        lock (prepared)
        {
            Assert.Contains("slow", prepared);
            Assert.DoesNotContain("never", prepared);
        }
        Assert.All(harness.Batches, batch => Assert.Equal("quick-tab", batch.TabName));
        Assert.All(harness.Progress, p => Assert.Equal("quick-tab", p.TabName));
        Assert.All(harness.Completions, c => Assert.Equal("quick-tab", c.TabName));
        Assert.Equal(["quick"], harness.Batches[^1].Ready.Select(i => i.Part.Name));
    }

    [Fact]
    public async Task StaleResultsPostedLateAreDiscarded()
    {
        // The other half of the staleness guard: callbacks already queued when the
        // switch happens must not be applied either.
        var harness = new Harness();
        var loader = harness.Create((_, _, _) => { });

        await loader.Start(Request("first", LazyPart("a")));
        // Nothing drained yet — everything the first job posted is still in flight.
        await loader.Start(Request("second", LazyPart("b")));
        harness.Drain();

        Assert.All(harness.Batches, batch => Assert.Equal("second", batch.TabName));
        Assert.All(harness.Completions, c => Assert.Equal("second", c.TabName));
    }

    [Fact]
    public async Task AFailedPartIsReportedAndTheRestOfTheTabStillLoads()
    {
        var harness = new Harness();
        var loader = harness.Create((part, _, _) =>
        {
            if (part.Name == "broken")
                throw new InvalidOperationException("no B-Rep route");
        });

        var parts = new[] { LazyPart("good"), LazyPart("broken"), LazyPart("also good") };
        await loader.Start(Request("model", parts));
        harness.Drain();

        var completion = Assert.Single(harness.Completions);
        var failure = Assert.Single(completion.Failures);
        Assert.Equal("broken", failure.PartName);
        Assert.Contains("no B-Rep route", failure.Message);
        Assert.Equal(2, completion.PartCount);          // the two that worked
        Assert.False(completion.Cancelled);

        // The broken part is out of the geometry (it has no mesh to upload), the others
        // are in, order preserved — and the bar still reached the end.
        var final = harness.Batches[^1];
        Assert.True(final.Final);
        Assert.Equal(["good", "also good"], final.Ready.Select(i => i.Part.Name));
        Assert.Equal(["broken"], final.Failed.Select(p => p.Name));
        Assert.All(harness.Batches, b => Assert.DoesNotContain(b.Ready, i => i.Part.Name == "broken"));
        Assert.Equal(1, harness.Progress[^1].Fraction, 9);
    }

    [Fact]
    public async Task CancelStopsPublishing()
    {
        var harness = new Harness();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var loader = harness.Create((part, _, _) =>
        {
            if (part.Name != "first")
                return;
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });

        var task = loader.Start(Request("model", LazyPart("first"), LazyPart("second")));
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        loader.Cancel();
        release.Set();
        await task;
        harness.Drain();

        // Only the empty opening batch (published before any work) ever arrives.
        Assert.All(harness.Batches, batch => Assert.Empty(batch.Ready));
        Assert.DoesNotContain(harness.Batches, batch => batch.Final);
        Assert.Empty(harness.Completions);
    }

    [Fact]
    public async Task SharedPartsAcrossInstancesAreMeshedOnce()
    {
        var harness = new Harness();
        var prepared = new List<string>();
        var loader = harness.Create((part, _, _) => prepared.Add(part.Name));

        // One part placed three times, as an assembly does.
        var bolt = LazyPart("bolt");
        var instances = new[]
        {
            new PartInstance(bolt, Matrix4d.Identity, "stack/bolt.1"),
            new PartInstance(bolt, Matrix4d.Identity, "stack/bolt.2"),
            new PartInstance(bolt, Matrix4d.Identity, "stack/bolt.3"),
        };
        await loader.Start(new TabMeshRequest("assembly", instances, null, Frame: true));
        harness.Drain();

        Assert.Equal(["bolt"], prepared);
        Assert.Equal(1, harness.Progress[0].Total);            // one part of work...
        Assert.Equal(3, harness.Batches[^1].Ready.Count);      // ... three instances shown
    }

    [Fact]
    public async Task SubPartProgressAdvancesTheBarWithinOnePart()
    {
        var harness = new Harness();
        var loader = harness.Create((_, _, progress) =>
        {
            for (int i = 1; i <= 10; i++)
                progress?.Report(i / 10.0);
        });

        await loader.Start(Request("model", LazyPart("only")));
        harness.Drain();

        // A single part still gives a moving bar (the SDF route reports fractions).
        var fractions = harness.Progress.Select(p => p.Fraction).ToList();
        Assert.True(fractions.Count > 2, $"expected sub-part progress, got {fractions.Count} reports");
        Assert.Equal(fractions.OrderBy(f => f), fractions);
        Assert.Equal(1, fractions[^1], 9);
    }

    [Fact]
    public async Task RealPartsMeshThroughTheDefaultPreparePath()
    {
        // No fake: the loader's default prepare is Part.Prepare, and a prepared part is
        // one the viewport can upload (mesh + feature edges cached, nothing left for
        // the render thread to compute).
        var batches = new List<TabMeshBatch>();
        var loader = new TabMeshLoader(callback => callback())   // inline "dispatcher"
        {
            Ready = batches.Add,
        };
        var part = LazyPart("block");
        Assert.False(part.HasMesh);

        await loader.Start(Request("model", part));
        Assert.True(part.HasMesh);
        Assert.NotEmpty(part.GetFeatureEdges());
        Assert.Contains(batches, b => b.Final && b.Ready.Count == 1);
    }
}
