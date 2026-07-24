using EngrCAD.Core;
using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Offscreen rendering of assembly instances: one shared <see cref="Part"/> placed at
/// several poses must appear at each pose (per-instance world matrices over one
/// uploaded mesh). Skips when no GL/EGL context is available. Shares the
/// "offscreen-gl" collection with <see cref="OffscreenRenderTests"/> so EGL contexts
/// are never created concurrently (ANGLE pbuffer creation is not thread-safe here).
/// </summary>
[Collection("offscreen-gl")]
public class AssemblyRenderTests
{
    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    [SkippableFact]
    public void SharedPart_RendersAtEveryInstancePose()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // One cube, instanced far left and far right; camera looks straight down -Y
        // (front view), so the instances land in the left and right image thirds and
        // the middle third above the grid stays background.
        var cube = new Part("cube", Shape.Box(1.5, 1.5, 1.5), Palette.Coral);
        var pair = new Assembly("pair");
        pair.Add(cube, Frame3d.FromXY((-4, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
        pair.Add(cube, Frame3d.FromXY((4, 0, 0), Vector3d.UnitX, Vector3d.UnitY));

        var scene = new Scene();
        scene.AddTab("t").Add(pair);
        scene.PreMesh();
        Assert.Single(scene.AllParts); // shared part meshed once

        const int w = 480, h = 320;
        var camera = new CameraState(-Math.PI / 2, 0.05, 14, Vector3d.Zero);
        var pixels = OffscreenRenderer.Render([.. scene.AllInstances], w, h, camera, furniture: false);

        // The coral part reads warm (R > B) against the bluish background regardless of
        // which face happens to catch the directional light.
        int leftWarm = 0, midWarm = 0, rightWarm = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (pixels[i] <= pixels[i + 2] + 10)
                    continue;
                if (x < w / 3) leftWarm++;
                else if (x < 2 * w / 3) midWarm++;
                else rightWarm++;
            }
        }

        Assert.True(leftWarm > 200, $"left instance missing ({leftWarm} warm pixels)");
        Assert.True(rightWarm > 200, $"right instance missing ({rightWarm} warm pixels)");
        Assert.True(midWarm < leftWarm / 4,
            $"unexpected part pixels in the middle ({midWarm}) — poses not applied per instance?");
    }

    [SkippableFact]
    public void NestedAssembly_PosesCompose_InRender()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // sub holds a cube at +x 4; outer poses sub rotated 90° about Z, so the cube
        // must appear at +y 4 (not +x 4). Camera looks down -X (right view): +y is
        // screen-left... use top-down instead: camera looking straight down, +x right,
        // +y up in image. The cube should land in the top half, not the right half.
        var cube = new Part("cube", Shape.Box(1.5, 1.5, 1.5), Palette.Coral);
        var sub = new Assembly("sub");
        sub.Add(cube, Frame3d.FromXY((4, 0, 0), Vector3d.UnitX, Vector3d.UnitY));
        var outer = new Assembly("outer");
        // 90° about Z: local X becomes world Y.
        outer.Add(sub, Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitY, -Vector3d.UnitX));

        var scene = new Scene();
        scene.AddTab("t").Add(outer);
        scene.PreMesh();

        const int w = 320, h = 320;
        var camera = new CameraState(-Math.PI / 2, Math.PI / 2 - 0.02, 14, Vector3d.Zero); // top view
        var pixels = OffscreenRenderer.Render([.. scene.AllInstances], w, h, camera, furniture: false);

        int topWarm = 0, bottomWarm = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (pixels[i] > pixels[i + 2] + 10) // warm coral vs bluish background
                {
                    if (y < h / 2) topWarm++;
                    else bottomWarm++;
                }
            }
        }

        Assert.True(topWarm > 200, $"rotated instance not in +y half (top {topWarm})");
        Assert.True(bottomWarm < topWarm / 4,
            $"geometry in the wrong half (bottom {bottomWarm}) — nested poses not composed?");
    }
}
