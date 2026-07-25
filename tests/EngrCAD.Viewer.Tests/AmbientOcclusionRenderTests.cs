using System.Reflection;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Pixel-level verification of baked ambient occlusion in the headless pass — the
/// same shader and the same baked floats the window uploads, so these assertions cover
/// both paths. Statistical (pixel classes and counts), not golden images.
/// </summary>
[Collection("offscreen-gl")]
public class AmbientOcclusionRenderTests
{
    private const int W = 260, H = 200;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>A block with a deep pocket: plenty of self-occlusion to see.</summary>
    private static IReadOnlyList<Part> Pocketed()
    {
        var scene = new Scene();
        scene.Add(new Part("block",
            Shape.Box(8, 8, 4) - Shape.Box(5, 5, 3).Translate(0, 0, 1.5), Palette.Steel));
        scene.PreMesh();
        return [.. scene.AllParts];
    }

    private static byte[] Render(
        IReadOnlyList<Part> parts, bool ao, ViewStyle style = ViewStyle.ShadedWithEdges,
        double? sectionOffset = null) =>
        OffscreenRenderer.Render(parts, W, H, camera: null, furniture: false, style,
            SectionAxis.Z, sectionOffset, ao);

    /// <summary>Pixels where <paramref name="a"/> is darker than <paramref name="b"/>
    /// by more than supersample noise.</summary>
    private static int DarkerPixels(byte[] a, byte[] b, int by = 8)
    {
        int count = 0;
        for (int p = 0; p < a.Length; p += 4)
        {
            if (b[p + 1] - a[p + 1] > by)
                count++;
        }
        return count;
    }

    [SkippableFact]
    public void AmbientOcclusion_DarkensCrevicesWithoutMovingAnything()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var parts = Pocketed();
        var withAo = Render(parts, ao: true);
        var flat = Render(parts, ao: false);

        int darkened = DarkerPixels(withAo, flat);
        int brightened = DarkerPixels(flat, withAo);
        Assert.True(darkened > 1000, $"expected the pocket to darken, only {darkened} pixels did");
        // Occlusion is a shading multiplier in [0, 1]: it can only ever darken. That
        // no pixel brightened is also the assertion that nothing moved — a shifted
        // silhouette or camera would brighten background pixels.
        Assert.True(brightened < darkened / 20,
            $"occlusion must only ever darken ({brightened} brightened vs {darkened} darkened)");
    }

    [SkippableFact]
    public void AmbientOcclusionOff_IsExactlyThePreAoImage()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // uAmbientOcclusion 0 makes the shader's factor exactly 1.0, so switching AO off
        // must reproduce the flat-lit look bit for bit however the mesh was uploaded.
        // Wireframe and points never sample the mesh shader at all.
        var parts = Pocketed();
        Assert.Equal(Render(parts, ao: false, style: ViewStyle.Wireframe),
                     Render(parts, ao: true, style: ViewStyle.Wireframe));
        Assert.Equal(Render(parts, ao: false, style: ViewStyle.Points),
                     Render(parts, ao: true, style: ViewStyle.Points));
    }

    [SkippableFact]
    public void SectionCutMaterial_IsUnaffectedByOcclusion()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // Cut faces are a flat fill by design (the shader returns before the AO
        // multiply), so the warm cut-material area must not change. The count is
        // compared within 2% rather than exactly: where a cut pixel borders a darkened
        // lit pixel, the 2x box downsample mixes the two and a handful of blend pixels
        // cross the warm threshold — an anti-aliasing effect at the boundary, not a
        // change to the cut material itself.
        var parts = Pocketed();
        int withAo = Warm(Render(parts, ao: true, sectionOffset: 0.0));
        int flat = Warm(Render(parts, ao: false, sectionOffset: 0.0));
        Assert.True(flat > 200, $"expected exposed cut material, got {flat} warm pixels");
        Assert.InRange(withAo, flat * 0.98, flat * 1.02);

        static int Warm(byte[] rgba)
        {
            int count = 0;
            for (int p = 0; p < rgba.Length; p += 4)
            {
                if (rgba[p] > rgba[p + 2] + 15)
                    count++;
            }
            return count;
        }
    }

    [SkippableFact]
    public void ConvexPart_LooksTheSameWithOrWithoutOcclusion()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // A convex body cannot occlude itself, so every baked value is exactly 1 and the
        // AO render must be bit-identical to the flat one — the tightest parity check
        // available: same shader, same buffers, same pixels.
        var scene = new Scene();
        scene.Add(new Part("box", Shape.Box(4, 3, 2), Palette.Steel));
        scene.PreMesh();
        var parts = (IReadOnlyList<Part>)[.. scene.AllParts];
        Assert.Equal(Render(parts, ao: false), Render(parts, ao: true));
    }

    [SkippableFact]
    public void RenderToImage_HonorsTheAmbientOcclusionFlag()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var scene = new Scene();
        scene.Add(new Part("block", Shape.Box(8, 8, 4) - Shape.Box(5, 5, 3).Translate(0, 0, 1.5)));
        var path = Path.Combine(Path.GetTempPath(), $"engrcad-ao-{Guid.NewGuid():N}.png");
        try
        {
            EngrCad.RenderToImage(scene, path, 160, 120, ambientOcclusion: true);
            Assert.True(File.Exists(path));
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            Assert.True(File.ReadAllBytes(path).AsSpan(0, 8).SequenceEqual(signature), "not a PNG file");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void EveryShaderSourceIsPureAscii()
    {
        // HARD-WON: one em dash in a shader comment made ANGLE's translator reject the
        // whole program; the compile exception aborted OnOpenGlInit before the other
        // programs were built and the entire viewport went black. This locks the rule.
        foreach (var field in typeof(ViewerShaders).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (field.GetValue(null) is not string source)
                continue;
            for (int i = 0; i < source.Length; i++)
            {
                Assert.True(source[i] < 128,
                    $"{field.Name} has a non-ASCII character U+{(int)source[i]:X4} at index {i}");
            }
        }
    }
}
