using EngrCAD.Modeling;
using EngrCAD.Viewer;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Headless offscreen rendering (<see cref="EngrCad.RenderToImage"/> /
/// <see cref="OffscreenRenderer"/>). Each test skips gracefully (via
/// <see cref="Skip.If"/>) when no GL/EGL context can be created — CI machines without a
/// GPU or ANGLE natives should not fail. The pixel buffer is RGBA8, top row first.
/// </summary>
public class OffscreenRenderTests
{
    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    private static Scene TwoPartScene()
    {
        var scene = new Scene();
        scene.Add(new Part("bracket", Shape.Box(4, 3, 1) - Shape.Cylinder(0.5, 3)));
        scene.Add(new Part("ball", Shape.Sphere(1).Translate(3, 0, 2)));
        scene.PreMesh();
        return scene;
    }

    private static (byte R, byte G, byte B) Pixel(byte[] rgba, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    [SkippableFact]
    public void Render_ProducesBackgroundGradientAndVisibleParts()
    {
        Skip.If(SkipReason is not null, SkipReason);

        const int w = 400, h = 300;
        var pixels = OffscreenRenderer.Render([.. TwoPartScene().AllParts], w, h);
        Assert.Equal(w * h * 4, pixels.Length);

        // Every pixel is fully opaque.
        for (int p = 0; p < w * h; p++)
            Assert.Equal(255, pixels[p * 4 + 3]);

        // Background gradient: dark bluish, brighter at the top than the bottom (the
        // viewer's vertical gradient). Corners sit outside the framed geometry.
        var top = Pixel(pixels, w, 3, 3);
        var bottom = Pixel(pixels, w, 3, h - 4);
        Assert.True(top.B > top.R && bottom.B >= bottom.R, "background should be bluish");
        Assert.True(top.R < 90 && top.G < 90, "top-left corner should be background, not a part");
        Assert.True(top.R + top.G + top.B > bottom.R + bottom.G + bottom.B,
            "the gradient should be brighter at the top");

        // The framed parts occupy the middle: a meaningful count of bright,
        // part-colored pixels, and the image is not a flat single color.
        int bright = 0;
        var distinct = new HashSet<int>();
        for (int p = 0; p < w * h; p++)
        {
            byte r = pixels[p * 4], g = pixels[p * 4 + 1], b = pixels[p * 4 + 2];
            if (r > 90 || g > 90)
                bright++;
            distinct.Add((r >> 3 << 10) | (g >> 3 << 5) | (b >> 3));
        }
        Assert.True(bright > w * h / 100, $"expected visible parts; only {bright} bright pixels");
        Assert.True(distinct.Count > 20, $"image looks flat: only {distinct.Count} distinct colors");
    }

    [SkippableFact]
    public void Render_NotAllBlack()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var pixels = OffscreenRenderer.Render([.. TwoPartScene().AllParts], 128, 128);
        Assert.Contains(pixels, b => b != 0);
        // The background alone is non-black; assert some pixel is clearly lit.
        bool anyLit = false;
        for (int p = 0; p < 128 * 128 && !anyLit; p++)
            anyLit = pixels[p * 4] > 100 || pixels[p * 4 + 1] > 100 || pixels[p * 4 + 2] > 100;
        Assert.True(anyLit, "no lit pixels — render is effectively black");
    }

    [SkippableFact]
    public void RenderToImage_WritesValidPng()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var path = Path.Combine(Path.GetTempPath(), $"engrcad-render-{Guid.NewGuid():N}.png");
        try
        {
            EngrCad.RenderToImage(TwoPartScene(), path, 320, 240);
            Assert.True(File.Exists(path));

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 1000, "PNG is suspiciously small");
            // PNG 8-byte signature.
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            Assert.True(bytes.AsSpan(0, 8).SequenceEqual(signature), "not a PNG file");
            // IHDR immediately follows the signature; bytes 16..20 are the width (big-endian).
            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            Assert.Equal(320, width);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [SkippableFact]
    public void Render_EmptyScene_IsAllBackground()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // No parts: a bare gradient with grid furniture, still a valid buffer.
        var pixels = OffscreenRenderer.Render([], 64, 64);
        Assert.Equal(64 * 64 * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }

    [SkippableFact]
    public void RenderRun_HeadlessSwitch_WritesPng()
    {
        Skip.If(SkipReason is not null, SkipReason);

        var path = Path.Combine(Path.GetTempPath(), $"engrcad-run-render-{Guid.NewGuid():N}.png");
        try
        {
            int code = EngrCad.Run(["--render", path], TwoPartScene);
            Assert.Equal(0, code);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
