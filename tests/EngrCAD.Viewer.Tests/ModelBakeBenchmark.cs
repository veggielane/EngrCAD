using System.Diagnostics;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// What a <c>$t</c> bake costs and what its cache buys — the two numbers the feature is
/// documented with, measured rather than asserted. Inert unless <c>ENGRCAD_BENCH</c> is
/// set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Viewer.Tests -c Release --filter FullyQualifiedName~ModelBakeBenchmark -l "console;verbosity=detailed"
/// </code>
/// The correctness half lives in <see cref="ModelAnimationTests"/>, which pins cached and
/// uncached bakes byte-identical — a speed claim about a cache means nothing without it.
/// </summary>
[Collection("offscreen-gl")]
public class ModelBakeBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    /// <summary>The docs fixture: a hoisted plate plus a column whose twist and taper
    /// follow t — the mesh route, so a frame is tessellation rather than a boolean.</summary>
    private static Func<double, Scene> TwistedColumn()
    {
        var plate = Shape.Box(60, 60, 5);
        var section = Sketch.Rectangle(22, 22);
        return t =>
        {
            var scene = new Scene();
            scene.Add(new Part("plate", plate));
            scene.Add(new Part("column",
                Shape.Extrude(section, 45, twist: 180 * t, scale: 1 - 0.6 * t, slices: 48)
                     .Translate(0, 0, 5)));
            return scene;
        };
    }

    /// <summary>The heavy fixture: a hoisted drilled plate plus a boss whose bore diameter
    /// follows t — a real B-Rep boolean every frame, which is where a bake stops being a
    /// tessellation cost and starts being a kernel one.</summary>
    private static Func<double, Scene> DrilledBoss()
    {
        var plate = Shape.Box(80, 50, 8)
            - Shape.Cylinder(3, 30).Translate(-30, 0, 0)
            - Shape.Cylinder(3, 30).Translate(30, 0, 0);
        return t =>
        {
            var scene = new Scene();
            scene.Add(new Part("plate", plate));
            scene.Add(new Part("boss",
                (Shape.Cylinder(12, 20) - Shape.Cylinder(4 + 4 * t, 40)).Translate(0, 0, 8)));
            return scene;
        };
    }

    /// <summary>The heaviest honest fixture: a body whose whole-solid ROUNDING radius
    /// follows t, so every frame runs the morphological opening plus a fresh
    /// tessellation. This is the end of the range the docs quote.</summary>
    private static Func<double, Scene> RoundedBlock()
    {
        var plate = Shape.Box(80, 50, 8);
        return t =>
        {
            var scene = new Scene();
            scene.Add(new Part("plate", plate));
            scene.Add(new Part("block",
                Shape.Box(30, 24, 18).RoundEdges(1 + 4 * t).Translate(0, 0, 8)));
            return scene;
        };
    }

    [SkippableFact]
    public void CachedVersusUncachedBake()
    {
        Skip.IfNot(Enabled, "set ENGRCAD_BENCH=1 to run");
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        const int frames = 24, width = 480, height = 360;

        // Warm both paths before measuring EITHER — a cold first row measures JIT tiering
        // rather than the arm it names (the repo's own recorded lesson).
        ModelAnimation.Bake(new TimeVaryingModel(TwistedColumn()), 2, width, height);
        ModelAnimation.Bake(new TimeVaryingModel(TwistedColumn(), cache: false), 2, width, height);

        var uncachedClock = Stopwatch.StartNew();
        var uncached = ModelAnimation.Bake(
            new TimeVaryingModel(TwistedColumn(), cache: false), frames, width, height);
        uncachedClock.Stop();

        var cachedClock = Stopwatch.StartNew();
        var cached = ModelAnimation.Bake(new TimeVaryingModel(TwistedColumn()), frames, width, height);
        cachedClock.Stop();

        output.WriteLine($"{frames} frames at {width}x{height} (hoisted plate + twisted column):");
        output.WriteLine($"  uncached : {uncachedClock.ElapsedMilliseconds} ms "
                       + $"({uncachedClock.Elapsed.TotalMilliseconds / frames:F1} ms/frame) — {uncached.Cache}");
        output.WriteLine($"  cached   : {cachedClock.ElapsedMilliseconds} ms "
                       + $"({cachedClock.Elapsed.TotalMilliseconds / frames:F1} ms/frame) — {cached.Cache}");
        output.WriteLine($"  speedup  : {uncachedClock.Elapsed.TotalMilliseconds / cachedClock.Elapsed.TotalMilliseconds:F2}x");
    }

    [SkippableFact]
    public void WhatOneFrameCostsAgainstAPoseAnimationsWholeClip()
    {
        Skip.IfNot(Enabled, "set ENGRCAD_BENCH=1 to run");
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        // The numbers the refusal to scrub rests on: ONE instant of a B-Rep model, with
        // the render excluded so it is the geometry cost alone. Two fixtures, because a
        // single figure would read as THE cost where it is really a range.
        output.WriteLine("one instant of a B-Rep model (lower + tessellate, no render):");
        foreach (var (name, factory) in new (string, Func<double, Scene>)[]
                 { ("boolean bore ", DrilledBoss()), ("whole-solid round", RoundedBlock()) })
        {
            var model = new TimeVaryingModel(factory);
            model.At(0);                    // warm: the hoisted plate is built once either way
            var clock = Stopwatch.StartNew();
            const int probes = 5;
            for (int i = 1; i <= probes; i++)
                model.At(i / (double)probes);
            clock.Stop();
            double perFrame = clock.Elapsed.TotalMilliseconds / probes;
            output.WriteLine($"  {name}: {perFrame:F0} ms/frame — {model.Cache}");
            output.WriteLine($"      a 60 Hz scrub has 16.7 ms; 24 frames is "
                           + $"{perFrame * 24 / 1000:F1} s of geometry alone.");
        }
    }

    [SkippableFact]
    public void WhatTheMeshRouteCostsPerFrame()
    {
        Skip.IfNot(Enabled, "set ENGRCAD_BENCH=1 to run");
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        // The docs figure's own per-frame geometry cost, cached and uncached, so the page
        // quotes what it measured rather than a plausible number.
        foreach (bool cache in new[] { false, true })
        {
            var model = new TimeVaryingModel(TwistedColumn(), cache: cache);
            model.At(0);
            var clock = Stopwatch.StartNew();
            const int probes = 8;
            for (int i = 1; i <= probes; i++)
                model.At(i / (double)probes);
            clock.Stop();
            output.WriteLine($"  cache {(cache ? "on " : "off")}: "
                           + $"{clock.Elapsed.TotalMilliseconds / probes:F1} ms/frame — {model.Cache}");
        }
    }
}
