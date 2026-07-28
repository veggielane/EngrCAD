using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The viewer side of the part-level debug modifiers: Ghost renders through the SAME
/// translucent path as an explicit <see cref="DisplayMode.Translucent"/> (byte-equal
/// offscreen frames — one code path, not a lookalike), and Hidden parts are excluded
/// from <see cref="EngrCad.RenderToImage"/> entirely (byte-equal to a scene that
/// never contained them).
/// </summary>
[Collection("offscreen-gl")]
public class DebugModifierRenderTests
{
    private const int W = 240, H = 180;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    [SkippableFact]
    public void GhostPart_RendersExactlyAsTranslucent()
    {
        Skip.If(SkipReason is not null, SkipReason);

        static byte[] Render(bool ghost)
        {
            var scene = new Scene();
            var part = scene.Add(new Part("box", Shape.Box(4, 3, 2), Palette.Steel));
            if (ghost)
                part.Ghost = true;
            else
                part.DisplayMode = DisplayMode.Translucent;
            scene.PreMesh();
            return OffscreenRenderer.Render(
                [.. scene.AllParts], W, H, camera: null, furniture: false, ViewStyle.ShadedWithEdges);
        }

        Assert.Equal(Render(ghost: false), Render(ghost: true));
    }

    [SkippableFact]
    public void HiddenPart_IsExcludedFromRenderToImage()
    {
        Skip.If(SkipReason is not null, SkipReason);

        static byte[] RenderScene(bool withHidden)
        {
            var scene = new Scene();
            if (withHidden)
            {
                var extra = scene.Add(new Part("extra", Shape.Box(6, 6, 6), Palette.Coral,
                    Matrix4d.CreateTranslation((5, 0, 0))));
                extra.Hidden = true;
            }
            scene.Add(new Part("keep", Shape.Box(2, 2, 2), Palette.Steel));
            var path = Path.Combine(Path.GetTempPath(), $"engrcad-debug-{Guid.NewGuid():N}.png");
            try
            {
                EngrCad.RenderToImage(scene, path, W, H);
                return File.ReadAllBytes(path);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        // Byte-equal PNGs: the hidden part must not even influence camera framing.
        Assert.Equal(RenderScene(withHidden: false), RenderScene(withHidden: true));
    }
}
