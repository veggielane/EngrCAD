using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The social-video presets: safe-area FRAMING as a geometric assertion (project the
/// eight bbox corners through the REAL view basis and require them inside the safe
/// rectangle, filling it on the binding axis), the duration cap as a refusal that names
/// the platform, and the aliasing check as a measurement with a hard Nyquist refusal.
/// </summary>
public class ReelExportTests
{
    /// <summary>A pose track spinning the scene's single instance about Z at a stated
    /// number of turns over the clip — the fast-mechanism stand-in the Nyquist check
    /// must fire on.</summary>
    private sealed class SpinTrack(Scene scene, double turns) : PoseTrack
    {
        private readonly List<PartInstance> _instances = [.. scene.Instances()];

        public override IReadOnlyList<PartInstance> PosesAt(double t)
        {
            double angle = t * turns * 2 * Math.PI;
            double c = Math.Cos(angle), s = Math.Sin(angle);
            var spin = new Matrix4d(
                c, -s, 0, 0,
                s, c, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1);
            return [.. _instances.Select(i => i with { World = spin * i.World })];
        }
    }

    private static Scene BoxScene(double x = 60, double y = 20, double z = 10)
    {
        var scene = new Scene();
        scene.Add(new Part("plate", Shape.Box(x, y, z)));
        scene.PreMesh();
        return scene;
    }

    private static (double MinX, double MaxX, double MinY, double MaxY) ProjectedBox(
        in Aabb bounds, CameraState camera, double aspect)
    {
        // The REAL render-path basis: LookAt's own rows, the Perspective projection's
        // own coefficients — so the framing solver is checked against the matrices the
        // renderer draws with, not against its own derivation.
        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var view = CameraMath.LookAt(eye, camera.Target, Vector3d.UnitZ);
        double k = 1.0 / Math.Tan(ReelFraming.FovY / 2);
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var corner in CornersOf(bounds))
        {
            var v = view.TransformPoint(corner);
            double w = -v.Z;
            Assert.True(w > 0, "a corner landed behind the eye");
            minX = Math.Min(minX, k / aspect * v.X / w);
            maxX = Math.Max(maxX, k / aspect * v.X / w);
            minY = Math.Min(minY, k * v.Y / w);
            maxY = Math.Max(maxY, k * v.Y / w);
        }
        return (minX, maxX, minY, maxY);
    }

    private static IEnumerable<Vector3d> CornersOf(Aabb b)
    {
        foreach (double cx in new[] { b.Min.X, b.Max.X })
            foreach (double cy in new[] { b.Min.Y, b.Max.Y })
                foreach (double cz in new[] { b.Min.Z, b.Max.Z })
                    yield return new Vector3d(cx, cy, cz);
    }

    [Theory]
    [InlineData("reel")]
    [InlineData("landscape")]
    public void CameraFor_PutsEveryCornerInsideTheSafeArea_AndFillsItsBindingAxis(string kind)
    {
        var format = kind == "reel" ? ReelFormat.InstagramReel : ReelFormat.YouTubeStandard;
        var bounds = new Aabb((-30, -10, -5), (30, 10, 5));
        var camera = ReelFraming.CameraFor(bounds, format);
        var (minX, maxX, minY, maxY) = ProjectedBox(bounds, camera, format.Aspect);

        double safeMinX = -1 + 2 * format.SafeLeft, safeMaxX = 1 - 2 * format.SafeRight;
        double safeMinY = -1 + 2 * format.SafeBottom, safeMaxY = 1 - 2 * format.SafeTop;
        const double slack = 1e-9;
        Assert.InRange(minX, safeMinX - slack, safeMaxX + slack);
        Assert.InRange(maxX, safeMinX - slack, safeMaxX + slack);
        Assert.InRange(minY, safeMinY - slack, safeMaxY + slack);
        Assert.InRange(maxY, safeMinY - slack, safeMaxY + slack);

        // "Frames INTO the safe area" means FILLING it on the binding axis — a camera
        // ten times too far away passes the inside test and fails this one.
        double fill = Math.Max(
            (maxX - minX) / (safeMaxX - safeMinX),
            (maxY - minY) / (safeMaxY - safeMinY));
        Assert.InRange(fill, 0.999, 1.0 + slack);

        // ... and the projected box is CENTRED in the safe rectangle, which for an
        // asymmetric one (captions below, rail right) is off the frame centre.
        Assert.InRange(Math.Abs((minX + maxX) / 2 - (safeMinX + safeMaxX) / 2), 0, 1e-6);
        Assert.InRange(Math.Abs((minY + maxY) / 2 - (safeMinY + safeMaxY) / 2), 0, 1e-6);
    }

    [Fact]
    public void ADurationPastThePlatformCap_IsRefusedNamingThePlatform()
    {
        var scene = BoxScene();
        var animation = new Animation(durationSeconds: 120).With(new SpinTrack(scene, 0.25));
        var thrown = Assert.Throws<ArgumentException>(() =>
            animation.RenderReel(scene, Path.GetTempPath(), ReelFormat.InstagramReel));
        Assert.Contains("Instagram Reel", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("90", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("120", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABodyPastNyquist_IsRefusedBeforeAFrameIsRendered()
    {
        // 0.1 s at 30 fps is 3 frames; two full turns over the clip is 2π per frame —
        // far past π, where the direction of rotation is not even represented. The
        // refusal must fire without a GL context, which is itself the assertion that
        // no frame was rendered first.
        var scene = BoxScene();
        var animation = new Animation(0.1).With(new SpinTrack(scene, turns: 2));
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            animation.RenderReel(scene, Path.GetTempPath(), ReelFormat.InstagramReel));
        Assert.Contains("Nyquist", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Slow the animation", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRotationMeasurementReadsTheSpinItWasGiven()
    {
        // A quarter turn over a 3-frame one-shot clip is π/4 per sample step (t = 0,
        // 0.5, 1 → π/8 per half... the track spins t·turns·2π, so consecutive samples
        // differ by turns·2π/(frames−1)). Asserted against the closed form.
        var scene = BoxScene();
        var animation = new Animation(0.1).With(new SpinTrack(scene, turns: 0.25));
        double measured = ReelExport.MaxRotationPerFrame(animation, scene, frames: 3, loop: false);
        Assert.InRange(Math.Abs(measured - 0.25 * 2 * Math.PI / 2), 0, 1e-12);

        // A pure TRANSLATION measures exactly zero however fast: translation does not
        // alias into reversed motion the way rotation does.
        var explodeScene = BoxScene();
        var still = new Animation(0.1).With(DeformationTracks.Constant(1));
        Assert.Equal(0, ReelExport.MaxRotationPerFrame(still, explodeScene, 3, loop: false));
    }
}

/// <summary>The GL half: a real (small) reel export and the safe-area poster.</summary>
[Collection("offscreen-gl")]
public class ReelExportRenderTests
{
    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    [SkippableFact]
    public void AShortClipExports_WithTheRecipeAndTheMeasurementOnTheResult()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = new Scene();
        scene.Add(new Part("plate", Shape.Box(40, 15, 6)));
        scene.PreMesh();
        var animation = new Animation(0.1).With(new ExplodeTrack(scene));

        string directory = Path.Combine(Path.GetTempPath(), $"engrcad-reel-{Guid.NewGuid():N}");
        try
        {
            var result = animation.RenderReel(
                scene, directory, ReelFormat.Portrait720, ambientOcclusion: false);
            Assert.Equal(3, result.Frames);   // 0.1 s x 30 fps
            Assert.Equal(3, result.FramePaths.Count);
            Assert.All(result.FramePaths, p => Assert.True(File.Exists(p)));
            Assert.Contains("libx264", result.FfmpegCommand, StringComparison.Ordinal);
            Assert.Contains("yuv420p", result.FfmpegCommand, StringComparison.Ordinal);
            Assert.Contains("scale=720:1280", result.FfmpegCommand, StringComparison.Ordinal);
            Assert.Equal(1, result.SlowdownFactorFor());   // nothing rotates
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [SkippableFact]
    public void ThePosterCarriesTheSafeAreaOverlay_AndWithoutItIsThePlainStill()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var scene = new Scene();
        scene.Add(new Part("plate", Shape.Box(40, 15, 6)));
        scene.PreMesh();
        var animation = new Animation(0.1).With(new ExplodeTrack(scene));

        string with = Path.Combine(Path.GetTempPath(), $"engrcad-poster-{Guid.NewGuid():N}.png");
        string without = Path.Combine(Path.GetTempPath(), $"engrcad-poster-{Guid.NewGuid():N}.png");
        try
        {
            animation.RenderReelPoster(scene, 0, with, ReelFormat.Portrait720,
                safeArea: true, ambientOcclusion: false);
            animation.RenderReelPoster(scene, 0, without, ReelFormat.Portrait720,
                safeArea: false, ambientOcclusion: false);
            var a = File.ReadAllBytes(with);
            var b = File.ReadAllBytes(without);
            Assert.NotEqual(Convert.ToHexString(a), Convert.ToHexString(b));
        }
        finally
        {
            File.Delete(with);
            File.Delete(without);
        }
    }
}
