using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Exploded views ride the SAME <see cref="PartInstance"/> flattening as everything else,
/// so a headless render at a factor is the window at that factor by construction. Pixel
/// level, because the claim being tested is that the offsets reach the render path and
/// separate the geometry — not merely that some math produced a vector. Shares the
/// "offscreen-gl" collection (no concurrent EGL contexts).
/// </summary>
[Collection("offscreen-gl")]
public class ExplodedRenderTests
{
    private const int W = 320, H = 240;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>A plate with two pins standing on it — enough structure that pulling it
    /// apart is visible as a change in the model's vertical extent.</summary>
    private static Scene Stack()
    {
        var scene = new Scene();
        var plate = new Part("plate", Shape.Box(20, 20, 2), Palette.Steel);
        var pin = new Part("pin", Shape.Cylinder(1.5, 4), Palette.Brass);
        var rig = new Assembly("rig");
        rig.Add(plate);
        rig.Add(pin, Frame3d.FromXY((-5, 0, 1), Vector3d.UnitX, Vector3d.UnitY));
        rig.Add(pin, Frame3d.FromXY((5, 0, 1), Vector3d.UnitX, Vector3d.UnitY));
        scene.AddTab("rig").Add(rig);
        return scene;
    }

    /// <summary>The rows (top and bottom) where the image stops being background —
    /// the model's vertical extent in pixels.</summary>
    private static (int Top, int Bottom) ModelRows(byte[] rgba)
    {
        int top = -1, bottom = -1;
        for (int row = 0; row < H; row++)
        {
            for (int x = 0; x < W; x++)
            {
                int p = (row * W + x) * 4;
                // The background gradient tops out around (46, 51, 61) and is always
                // blue-leaning; any brighter or warmer pixel is geometry.
                if (rgba[p] > 70 || rgba[p] >= rgba[p + 2])
                {
                    if (top < 0)
                        top = row;
                    bottom = row;
                    break;
                }
            }
        }
        return (top, bottom);
    }

    private static byte[] Render(Scene scene, double explode)
    {
        scene.PreMesh();
        if (explode != 0)
            scene.AutoExplode();
        return OffscreenRenderer.Render(
            [.. scene.Instances(explode)], W, H,
            // Straight along -Y: the stacking axis is vertical in the image.
            new CameraState(Math.PI / 2, 0, 60, (0, 0, 0)),
            furniture: false, ViewStyle.ShadedWithEdges, ambientOcclusion: false);
    }

    [SkippableFact]
    public void ExplodingSeparatesTheModelVertically()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var scene = Stack();

        var (assembledTop, assembledBottom) = ModelRows(Render(scene, 0));
        var (explodedTop, explodedBottom) = ModelRows(Render(scene, 1));

        Assert.True(assembledTop >= 0 && explodedTop >= 0, "the model should be visible in both renders");
        int assembled = assembledBottom - assembledTop;
        int exploded = explodedBottom - explodedTop;
        // The pins lift clear of the plate along the datum-radial direction; the plate
        // itself is the datum and stays, so the model gets taller, not merely different.
        Assert.True(exploded > assembled * 1.5,
            $"exploding should stretch the model ({assembled} px assembled, {exploded} px exploded)");
    }

    [SkippableFact]
    public void FactorZeroRendersExactlyTheAssembledScene()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var scene = Stack();
        scene.PreMesh();
        var plain = OffscreenRenderer.Render(
            [.. scene.AllInstances], W, H, new CameraState(Math.PI / 2, 0, 60, (0, 0, 0)),
            furniture: false, ViewStyle.ShadedWithEdges, ambientOcclusion: false);

        scene.AutoExplode();                        // offsets exist...
        var atZero = OffscreenRenderer.Render(
            [.. scene.Instances(0)], W, H, new CameraState(Math.PI / 2, 0, 60, (0, 0, 0)),
            furniture: false, ViewStyle.ShadedWithEdges, ambientOcclusion: false);

        Assert.Equal(plain, atZero);                // ...and factor 0 ignores them, byte for byte
    }

    [SkippableFact]
    public void RenderToImageTakesTheFactorAndDerivesOffsetsItself()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var scene = Stack();
        string assembled = Path.Combine(Path.GetTempPath(), $"engrcad-assembled-{Guid.NewGuid():N}.png");
        string exploded = Path.Combine(Path.GetTempPath(), $"engrcad-exploded-{Guid.NewGuid():N}.png");
        try
        {
            EngrCad.RenderToImage(scene, assembled, W, H, ambientOcclusion: false);
            EngrCad.RenderToImage(scene, exploded, W, H, ambientOcclusion: false, explode: 1);

            // Nothing set the offsets: RenderToImage derived them because a factor was asked for.
            Assert.All(scene.Tabs[0].Assemblies.SelectMany(a => a.Occurrences).Skip(1),
                o => Assert.NotNull(o.ExplodeOffset));
            Assert.NotEqual(File.ReadAllBytes(assembled), File.ReadAllBytes(exploded));
        }
        finally
        {
            File.Delete(assembled);
            File.Delete(exploded);
        }
    }
}
