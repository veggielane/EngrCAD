using System.Diagnostics;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// What batching an animation export buys: one EGL context, one set of linked programs
/// and one set of uploaded per-part buffers for the whole clip, against the
/// one-context-per-frame loop it replaced. Inert unless <c>ENGRCAD_BENCH</c> is set:
/// <code>
/// $env:ENGRCAD_BENCH = "1"
/// dotnet test tests/EngrCAD.Viewer.Tests -c Release --filter FullyQualifiedName~AnimationExportBenchmark -l "console;verbosity=detailed"
/// </code>
/// The correctness half lives in <see cref="AnimationStillTests"/>, which pins the two
/// paths byte-identical — a speed claim about a render path means nothing without it.
/// </summary>
[Collection("offscreen-gl")]
public class AnimationExportBenchmark(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ENGRCAD_BENCH") is not (null or "");

    [SkippableFact]
    public void BatchedVersusPerFrameContext()
    {
        Skip.IfNot(Enabled, "set ENGRCAD_BENCH=1 to run");
        Skip.If(!OffscreenRenderer.IsAvailable, OffscreenRenderer.UnavailableReason);

        // A scene with enough per-part upload work to be worth measuring: a drilled
        // plate, a filleted block and two pins, in an assembly that explodes.
        var scene = new Scene();
        var plate = new Part("plate", Shape.Box(40, 30, 6)
            - Shape.Cylinder(3, 20).Translate(-12, 0, 0)
            - Shape.Cylinder(3, 20).Translate(12, 0, 0));
        var block = new Part("block", Shape.Box(14, 10, 8).RoundEdges(1.5));
        var pin = new Part("pin", Shape.Cylinder(2, 12));
        var rig = new Assembly("rig");
        rig.Add(plate);
        rig.Add(block, Frame3d.FromXY((0, 0, 7), Vector3d.UnitX, Vector3d.UnitY));
        rig.Add(pin, Frame3d.FromXY((-12, 0, 3), Vector3d.UnitX, Vector3d.UnitY));
        rig.Add(pin, Frame3d.FromXY((12, 0, 3), Vector3d.UnitX, Vector3d.UnitY));
        scene.AddTab("rig").Add(rig);
        scene.PreMesh();

        const int frames = 24, width = 480, height = 360;
        var animation = new Animation(durationSeconds: 2).With(new ExplodeTrack(scene));
        var bounds = Aabb.Empty;
        foreach (var instance in animation.At(1).Instances!)
            bounds = bounds.Union(instance.Bounds());
        var camera = CameraMath.DefaultCamera(bounds);

        var timeline = new List<(IReadOnlyList<PartInstance>, CameraState)>(frames);
        for (int i = 0; i < frames; i++)
            timeline.Add((animation.At(i / (double)frames).Instances!, camera));

        // Warm up both paths (JIT tiering makes a single cold run meaningless — the
        // repo's own recorded lesson).
        OffscreenRenderer.RenderSequence(timeline.Take(2).ToList(), width, height);
        OffscreenRenderer.Render(timeline[0].Item1, width, height, camera);

        var perFrame = Stopwatch.StartNew();
        foreach (var (instances, cam) in timeline)
            OffscreenRenderer.Render(instances, width, height, cam);
        perFrame.Stop();

        var batched = Stopwatch.StartNew();
        OffscreenRenderer.RenderSequence(timeline, width, height);
        batched.Stop();

        output.WriteLine($"{frames} frames at {width}x{height}:");
        output.WriteLine($"  per-frame context : {perFrame.ElapsedMilliseconds} ms");
        output.WriteLine($"  batched           : {batched.ElapsedMilliseconds} ms");
        output.WriteLine($"  speedup           : {perFrame.Elapsed.TotalMilliseconds / batched.Elapsed.TotalMilliseconds:F2}x");
    }
}
