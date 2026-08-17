using EngrCAD.Core;
using EngrCAD.Mesh;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The <c>$t</c> model's geometry cache: what it saves, what it must never change, and
/// the one thing that makes a transplant unsound.
/// <para>The bar these tests hold is the one a cache has to meet before it may exist:
/// <b>the answer is the same with it and without it</b>. Everything else here is
/// accounting.</para>
/// </summary>
public class TimeVaryingModelTests
{
    /// <summary>A plate that never changes plus a column whose taper follows t — the
    /// shape of every real time-varying model, and the one the hit rate is measured on.
    /// The plate's Shape is HOISTED (built once, captured), which is what the cache keys
    /// on; the column's is rebuilt per frame because it genuinely differs.</summary>
    private static Func<double, Scene> PlateAndColumn(out Shape plate)
    {
        var hoisted = Shape.Box(40, 40, 4);
        plate = hoisted;
        return t =>
        {
            var scene = new Scene();
            scene.Add(new Part("plate", hoisted));
            scene.Add(new Part("column",
                Shape.Cylinder(8 - 4 * t, 20).Translate(0, 0, 4)));
            return scene;
        };
    }

    private static long[] VertexBits(HalfEdgeMesh mesh)
    {
        var (positions, _) = mesh.ToIndexed();
        var bits = new long[positions.Length * 3];
        for (int i = 0; i < positions.Length; i++)
        {
            bits[i * 3] = BitConverter.DoubleToInt64Bits(positions[i].X);
            bits[i * 3 + 1] = BitConverter.DoubleToInt64Bits(positions[i].Y);
            bits[i * 3 + 2] = BitConverter.DoubleToInt64Bits(positions[i].Z);
        }
        return bits;
    }

    private static long[] SceneBits(Scene scene) =>
        [.. scene.AllParts.OrderBy(p => p.Name, StringComparer.Ordinal)
            .SelectMany(p => VertexBits(p.GetMesh()))];

    [Fact]
    public void TheSameInstantProducesTheSameGeometryBitForBit()
    {
        var model = new TimeVaryingModel(PlateAndColumn(out _));
        // Two calls at one t: different Part objects, and the factory is a pure function
        // of t, so the geometry must agree to the last bit rather than to a tolerance.
        Assert.Equal(SceneBits(model.At(0.37)), SceneBits(model.At(0.37)));
    }

    [Fact]
    public void AWholeBakeIsReproducible()
    {
        double[] instants = [0, 0.25, 0.5, 0.75, 1];
        // The trajectory, not the endpoint: two runs could land on one shape by different
        // routes, and only the whole sequence would show it.
        var first = instants.Select(t => SceneBits(new TimeVaryingModel(PlateAndColumn(out _)).At(t))).ToList();
        var second = instants.Select(t => SceneBits(new TimeVaryingModel(PlateAndColumn(out _)).At(t))).ToList();
        for (int i = 0; i < instants.Length; i++)
            Assert.Equal(first[i], second[i]);
    }

    [Fact]
    public void AHoistedPartIsBuiltOnceAndReusedEveryFrameAfterward()
    {
        var model = new TimeVaryingModel(PlateAndColumn(out _));
        const int frames = 5;
        for (int i = 0; i < frames; i++)
            model.At(i / (double)(frames - 1));

        var report = model.Cache;
        Assert.Equal(frames, report.Frames);
        Assert.Equal(2 * frames, report.Parts);
        // The plate is built once and reused four times; the column is rebuilt every
        // frame because its geometry genuinely changes. So the hit rate is exactly the
        // share of part-visits the hoist covers.
        Assert.Equal(frames + 1, report.Built);
        Assert.Equal(frames - 1, report.Reused);
        Assert.Equal((frames - 1) / (double)(2 * frames), report.HitRate, 12);
    }

    [Fact]
    public void AModelWhoseGeometryNeverChangesHitsTheCacheEveryFrameAfterTheFirst()
    {
        var body = Shape.Box(10, 10, 10);
        var model = new TimeVaryingModel(t =>
        {
            var scene = new Scene();
            // A model that says nothing about t: every frame is the same geometry object.
            scene.Add(new Part("body", body));
            return scene;
        });
        const int frames = 6;
        for (int i = 0; i < frames; i++)
            model.At(i / (double)(frames - 1));

        Assert.Equal(1, model.Cache.Built);
        Assert.Equal(frames - 1, model.Cache.Reused);
    }

    [Fact]
    public void AModelThatRebuildsEveryFrameNeverHitsTheCache()
    {
        // The complement, and the reason the previous test means something: with nothing
        // hoisted there is no shared object, so the hit rate is 0 and the report says so
        // rather than flattering the feature.
        var model = new TimeVaryingModel(t =>
        {
            var scene = new Scene();
            scene.Add(new Part("body", Shape.Box(10 + t, 10, 10)));
            return scene;
        });
        const int frames = 6;
        for (int i = 0; i < frames; i++)
            model.At(i / (double)(frames - 1));

        Assert.Equal(frames, model.Cache.Built);
        Assert.Equal(0, model.Cache.Reused);
        Assert.Equal(0, model.Cache.HitRate);
    }

    [Fact]
    public void TheCacheDoesNotChangeTheAnswer()
    {
        var cached = new TimeVaryingModel(PlateAndColumn(out _));
        var uncached = new TimeVaryingModel(PlateAndColumn(out _), cache: false);
        foreach (double t in new[] { 0, 0.2, 0.5, 0.8, 1.0 })
        {
            // The assertion the cache exists to survive: a transplanted mesh is the very
            // object the frame's own lowering would have produced, so this is bit
            // equality rather than agreement.
            Assert.Equal(SceneBits(uncached.At(t)), SceneBits(cached.At(t)));
        }
        Assert.True(cached.Cache.Reused > 0);
        Assert.Equal(0, uncached.Cache.Reused);
    }

    [Fact]
    public void TheCacheKeyIncludesTheMeshQuality()
    {
        var model = new TimeVaryingModel(VaryingQuality());
        int coarse = model.At(0).AllParts.Single().GetMesh().FaceCount;
        int fine = model.At(1).AllParts.Single().GetMesh().FaceCount;
        // One geometry object at two resolutions: the second frame must not be handed the
        // first's mesh, so the two disagree.
        Assert.True(fine > coarse,
            $"a finer quality must produce a finer mesh (coarse {coarse}, fine {fine})");
    }

    [Fact]
    public void WithoutTheQualityInTheKeyACoarseMeshStandsInForAFineOne()
    {
        // The mutation, run deliberately: drop the quality from the key and the same
        // geometry object at a finer quality silently adopts the coarse mesh. Shown to
        // fire, because a transplant guard with no compiler behind it is otherwise a
        // claim rather than a check.
        var model = new TimeVaryingModel(VaryingQuality()) { KeyOnQuality = false };
        int coarse = model.At(0).AllParts.Single().GetMesh().FaceCount;
        int fine = model.At(1).AllParts.Single().GetMesh().FaceCount;
        Assert.Equal(coarse, fine);
    }

    /// <summary>One hoisted geometry rendered at two different qualities — coarse below
    /// the halfway mark, fine above it.</summary>
    private static Func<double, Scene> VaryingQuality()
    {
        var body = Shape.Cylinder(10, 20);
        return t =>
        {
            var scene = new Scene(new MeshQuality { SegmentsPerCircle = t < 0.5 ? 16 : 64 });
            scene.Add(new Part("body", body));
            return scene;
        };
    }

    [Fact]
    public void AMorphingPartsPerFrameGeometryDoesNotAccumulateInTheCache()
    {
        var model = new TimeVaryingModel(PlateAndColumn(out _));
        for (int i = 0; i < 12; i++)
            model.At(i / 11.0);

        // The eviction rule's whole claim: an entry the most recent frame did not touch is
        // dropped, so the cache holds ONE frame's geometries rather than the clip's. The
        // hoisted plate is present in every frame and survives; the column's eleven
        // superseded shapes are gone. Twelve frames, two live entries.
        Assert.Equal(2, model.CachedEntryCount);
        Assert.True(model.Cache.Reused > 0, "the fixture must still be hitting the cache");
    }

    [Fact]
    public void AHistoryBackedPartComposesWithTheModelCache()
    {
        // The second mechanism, at its own granularity: driving a [Param] re-runs only the
        // TAIL of the history, while the geometry cache sees a FRESH body object each frame
        // (a regeneration produces one) and so correctly reports a miss. The two savings
        // are independent, which is what "they compose" means — and this pins that the model
        // does not somehow defeat the prefix cache.
        var history = new FeatureHistory();
        history.Add(new ExtrudeSketchFeature(Sketch.Rectangle(40, 30)) { Height = 10 });
        history.Add(new HoleFeature(StandardHoles.Clearance(5), [new Vector2d(0, 0)]) { Depth = 6 });
        var part = history.ToPart("plate");

        var model = new TimeVaryingModel(t =>
        {
            // A fresh instance is how a [Param] is driven (they are init-only).
            history.Replace(1, new HoleFeature(StandardHoles.Clearance(5), [new Vector2d(0, 0)])
            {
                Depth = 6 + 2 * t,
            });
            var result = part.Regenerate();
            Assert.True(result.Succeeded, result.ToString());
            // The plate above the change is REUSED by the history's own prefix cache.
            Assert.Equal(FeatureOutcome.Cached, result.Statuses[0].Outcome);
            Assert.Equal(FeatureOutcome.Applied, result.Statuses[1].Outcome);
            var scene = new Scene();
            scene.Add(part);
            return scene;
        });

        for (int i = 0; i < 3; i++)
            model.At(i / 2.0);

        // A regenerated body is a fresh Shape object, so the geometry cache honestly
        // reports no hit — the saving lived one layer up, in the history.
        Assert.Equal(3, model.Cache.Built);
        Assert.Equal(0, model.Cache.Reused);
    }

    [Fact]
    public void AdoptingDerivedGeometryFromADifferentShapeIsRefusedByName()
    {
        var a = new Part("a", Shape.Box(10, 10, 10));
        var b = new Part("b", Shape.Box(10, 10, 10));   // equal, NOT the same object
        a.GetMesh();
        var error = Assert.Throws<InvalidOperationException>(() => b.AdoptDerivedFrom(a));
        Assert.Contains("'b'", error.Message);
        Assert.Contains("'a'", error.Message);
        Assert.Contains("different geometry objects", error.Message);
    }

    [Fact]
    public void ClearCacheReleasesTheCachesAndResetsTheReport()
    {
        var model = new TimeVaryingModel(PlateAndColumn(out _));
        model.At(0);
        model.At(1);
        Assert.True(model.Cache.Reused > 0);

        model.ClearCache();
        Assert.Equal(new ModelCacheReport(0, 0, 0), model.Cache);
        model.At(0.5);
        // A cleared cache rebuilds: the plate is a miss again.
        Assert.Equal(2, model.Cache.Built);
        Assert.Equal(0, model.Cache.Reused);
    }

    [Fact]
    public void AFactoryThatReturnsNoSceneIsRefusedByName()
    {
        var model = new TimeVaryingModel(_ => null!);
        var error = Assert.Throws<InvalidOperationException>(() => model.At(0.5));
        Assert.Contains("0.5", error.Message);
        Assert.Contains("must produce a Scene", error.Message);
    }

    [Fact]
    public void AFactoryIsRequired() =>
        Assert.Throws<ArgumentNullException>(() => new TimeVaryingModel(null!));

    [Fact]
    public void AReusedPartCarriesItsOwnIdentityAndTheSharedGeometry()
    {
        // The transplant moves DERIVED caches only: a frame's part keeps its own name,
        // colour and transform, so a factory is free to place one hoisted shape
        // differently at every instant and still pay for the mesh once.
        var body = Shape.Cylinder(4, 10);
        var model = new TimeVaryingModel(t =>
        {
            var scene = new Scene();
            scene.Add(new Part($"pin at {t:0.##}", body,
                color: null, transform: Matrix4d.CreateTranslation(new Vector3d(10 * t, 0, 0))));
            return scene;
        });
        var first = model.At(0).AllParts.Single();
        var second = model.At(1).AllParts.Single();

        Assert.Equal(1, model.Cache.Built);
        Assert.Equal(1, model.Cache.Reused);
        Assert.NotEqual(first.Name, second.Name);
        Assert.NotEqual(
            first.Transform.TransformPoint(Vector3d.Zero), second.Transform.TransformPoint(Vector3d.Zero));
        // Same mesh OBJECT, which is what makes the reuse free downstream too: the
        // renderer's per-part upload cache keys on the Part, and the mesh behind it is
        // literally shared.
        Assert.Same(first.GetMesh(), second.GetMesh());
    }
}
