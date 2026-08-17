using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Baking a <see cref="TimeVaryingModel"/> — the <c>$t</c> path. The oracle is the one
/// every playback rung in this repo is held to: <b>a frame of the clip is byte-identical
/// to a still of the same configuration</b>, which is available here exactly as it is for
/// a deformation scalar or a field step, because a bake changes what a frame CONTAINS but
/// not how it is drawn.
/// <para>The second bar is the cache's: a bake with it and a bake without it must produce
/// the same bytes. A cache that changes output is the failure mode this file exists for.</para>
/// </summary>
[Collection("offscreen-gl")]
public class ModelAnimationTests
{
    private const int Width = 200, Height = 150;

    /// <summary>A static plate carrying a column whose taper follows t: the plate's shape
    /// is HOISTED so it caches, the column's is not because it genuinely changes.</summary>
    private static Func<double, Scene> PlateAndColumn()
    {
        var plate = Shape.Box(40, 40, 4);
        return t =>
        {
            var scene = new Scene(new MeshQuality { SegmentsPerCircle = 24 });
            scene.Add(new Part("plate", plate, new PartColor(0.55f, 0.58f, 0.62f)));
            scene.Add(new Part("column", Shape.Cylinder(9 - 5 * t, 22).Translate(0, 0, 4)));
            return scene;
        };
    }

    [SkippableFact]
    public void EveryFrameIsByteIdenticalToAStillOfTheSameConfiguration()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        const int frames = 5;
        var baked = ModelAnimation.Bake(
            new TimeVaryingModel(PlateAndColumn()), frames, Width, Height,
            ambientOcclusion: false);

        // The configuration a frame is drawn with is its instances, the clip's one camera
        // AND the clip's one furniture box — so the still is given all three. Rebuilding
        // the scenes through a SECOND model is deliberate: the comparison then also says
        // the factory is a pure function of t.
        var still = new TimeVaryingModel(PlateAndColumn());
        var bounds = Aabb.Empty;
        var perFrame = new List<IReadOnlyList<PartInstance>>();
        for (int i = 0; i < frames; i++)
        {
            var instances = still.At(i / (double)(frames - 1)).Instances().ToList();
            perFrame.Add(instances);
            foreach (var instance in instances)
                bounds = bounds.Union(instance.Bounds());
        }

        for (int i = 0; i < frames; i++)
        {
            var single = OffscreenRenderer.Render(
                perFrame[i], Width, Height, baked.Camera, ambientOcclusion: false, sceneBounds: bounds);
            Assert.Equal(single, baked.Frames[i]);
        }
    }

    [SkippableFact]
    public void TheCacheDoesNotChangeAnyFrame()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        const int frames = 4;
        var cached = ModelAnimation.Bake(
            new TimeVaryingModel(PlateAndColumn()), frames, Width, Height, ambientOcclusion: false);
        var uncached = ModelAnimation.Bake(
            new TimeVaryingModel(PlateAndColumn(), cache: false), frames, Width, Height,
            ambientOcclusion: false);

        Assert.True(cached.Cache.Reused > 0, "the fixture must exercise the cache");
        Assert.Equal(0, uncached.Cache.Reused);
        for (int i = 0; i < frames; i++)
            Assert.Equal(uncached.Frames[i], cached.Frames[i]);
    }

    [SkippableFact]
    public void AModelWhoseGeometryDoesNotChangeBakesIdenticalFramesAndHitsTheCacheEveryTime()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        var body = Shape.Box(20, 20, 20);
        var model = new TimeVaryingModel(_ =>
        {
            var scene = new Scene();
            scene.Add(new Part("body", body));
            return scene;
        });
        var baked = ModelAnimation.Bake(model, frames: 4, Width, Height, ambientOcclusion: false);

        // Nothing moves and nothing morphs, so every frame is the same picture — and the
        // cache built exactly one mesh for the four of them.
        foreach (var frame in baked.Frames)
            Assert.Equal(baked.Frames[0], frame);
        Assert.Equal(1, baked.Cache.Built);
        Assert.Equal(3, baked.Cache.Reused);
    }

    [SkippableFact]
    public void AModelThatChangesEveryFrameNeverHitsTheCacheAndDrawsDifferentPictures()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        // The complement that makes the previous test mean something: with nothing shared
        // the hit rate is 0, and the pictures genuinely differ.
        var baked = ModelAnimation.Bake(
            new TimeVaryingModel(t =>
            {
                var scene = new Scene();
                scene.Add(new Part("body", Shape.Box(20, 20, 6 + 20 * t)));
                return scene;
            }),
            frames: 4, Width, Height, ambientOcclusion: false);

        Assert.Equal(4, baked.Cache.Built);
        Assert.Equal(0, baked.Cache.Reused);
        Assert.NotEqual(baked.Frames[0], baked.Frames[3]);
    }

    [SkippableFact]
    public void TwoBakesOfOneModelAreByteIdentical()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        var first = ModelAnimation.Bake(
            new TimeVaryingModel(PlateAndColumn()), frames: 4, Width, Height, ambientOcclusion: false);
        var second = ModelAnimation.Bake(
            new TimeVaryingModel(PlateAndColumn()), frames: 4, Width, Height, ambientOcclusion: false);
        for (int i = 0; i < first.Frames.Count; i++)
            Assert.Equal(first.Frames[i], second.Frames[i]);
        Assert.Equal(first.Camera, second.Camera);
    }

    [SkippableFact]
    public void TheClipIsFramedOverEveryFrameRatherThanItsEnds()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        // A model that is widest in the MIDDLE: an animation's first-union-last framing is
        // right for an explode (whose extremes bracket it) and would crop this, which is
        // why a bake unions every frame it builds.
        var baked = ModelAnimation.Bake(
            new TimeVaryingModel(Bulge), frames: 5, Width, Height, ambientOcclusion: false);

        var ends = Aabb.Empty;
        var endsModel = new TimeVaryingModel(Bulge);
        foreach (double t in new[] { 0.0, 1.0 })
        {
            foreach (var instance in endsModel.At(t).Instances())
                ends = ends.Union(instance.Bounds());
        }
        var endsCamera = CameraMath.DefaultCamera(ends);
        Assert.True(baked.Camera.Distance > endsCamera.Distance,
            "the union of every frame must pull the camera back past the ends' own framing "
            + $"({baked.Camera.Distance:F2} vs {endsCamera.Distance:F2})");

        static Scene Bulge(double t)
        {
            var scene = new Scene();
            scene.Add(new Part("bulge", Shape.Box(20, 20, 10 + 60 * Math.Sin(Math.PI * t))));
            return scene;
        }
    }

    [SkippableFact]
    public void AnApngBakeWritesAPlayableFileAndReportsItsCache()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        string path = Path.Combine(Path.GetTempPath(), $"engrcad-model-{Guid.NewGuid():N}.png");
        try
        {
            var baked = new TimeVaryingModel(PlateAndColumn())
                .RenderApng(path, frames: 3, durationSeconds: 1, Width, Height, ambientOcclusion: false);
            Assert.Equal(3, baked.Frames.Count);
            Assert.True(baked.Cache.Reused > 0);
            var bytes = File.ReadAllBytes(path);
            // An APNG IS a PNG whose animation chunks a player reads; the writer's own
            // round-trip tests own the format, so this asserts only that a bake produced one.
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes.Take(4));
            Assert.Contains("acTL", System.Text.Encoding.ASCII.GetString(bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void AFrameSequenceBakeWritesNumberedPngs()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        string dir = Path.Combine(Path.GetTempPath(), $"engrcad-model-{Guid.NewGuid():N}");
        try
        {
            new TimeVaryingModel(PlateAndColumn())
                .RenderFrames(dir, frames: 3, Width, Height, ambientOcclusion: false);
            Assert.True(File.Exists(Path.Combine(dir, "frame-0000.png")));
            Assert.True(File.Exists(Path.Combine(dir, "frame-0002.png")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void StatingTheFramesOwnBoundsIsExactlyTheIncumbentArithmetic()
    {
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        // `sceneBounds` overrides what the grid and the frustum planes are read off. Null
        // is what every incumbent caller passes, and the value null COMPUTES is the
        // instances' own union — so handing that same box in explicitly must change
        // nothing at all. This is what makes the parameter safe to have added: every
        // animation export, still and window render still takes the arithmetic it took.
        var instances = new TimeVaryingModel(PlateAndColumn()).At(0.4).Instances().ToList();
        var camera = CameraMath.DefaultCamera(
            instances.Aggregate(Aabb.Empty, (box, i) => box.Union(i.Bounds())));
        var own = instances.Aggregate(Aabb.Empty, (box, i) => box.Union(i.Bounds()));

        Assert.Equal(
            OffscreenRenderer.Render(instances, Width, Height, camera, ambientOcclusion: false),
            OffscreenRenderer.Render(instances, Width, Height, camera, ambientOcclusion: false,
                sceneBounds: own));
    }

    [Fact]
    public void ABakeNeedsAtLeastTwoFrames()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => ModelAnimation.Bake(new TimeVaryingModel(PlateAndColumn()), frames: 1));
        Assert.Contains("at least two frames", error.Message);
    }

    [Fact]
    public void ABakeNeedsAModel() =>
        Assert.Throws<ArgumentNullException>(() => ModelAnimation.Bake(null!, frames: 4));

    [Fact]
    public void AClipNeedsAPositiveDuration() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TimeVaryingModel(PlateAndColumn()).RenderApng("unused.png", frames: 4, durationSeconds: 0));
}
