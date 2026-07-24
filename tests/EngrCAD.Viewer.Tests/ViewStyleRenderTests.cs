using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Pixel-level verification that the offscreen pass honors the global view style,
/// per-part display modes, and axis-aligned section planes — the "headless renders
/// match the window" contract. Assertions are statistical (pixel counts and color
/// classes), not golden images, so mesh-tessellation changes don't break them.
/// GL-touching test classes share the "offscreen-gl" collection so EGL contexts are
/// never created concurrently.
/// </summary>
[Collection("offscreen-gl")]
public class ViewStyleRenderTests
{
    private const int W = 240, H = 180;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>One steel box, no furniture: every non-background pixel is the part.</summary>
    private static IReadOnlyList<Part> Box(DisplayMode mode = DisplayMode.Shaded)
    {
        var scene = new Scene();
        var part = scene.Add(new Part("box", Shape.Box(4, 3, 2)));
        part.DisplayMode = mode;
        scene.PreMesh();
        return [.. scene.AllParts];
    }

    private static byte[] Render(
        IReadOnlyList<Part> parts, ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis axis = SectionAxis.Z, double? sectionOffset = null) =>
        OffscreenRenderer.Render(parts, W, H, camera: null, furniture: false, style, axis, sectionOffset);

    /// <summary>Pixels that are clearly bright steel (fills or full-intensity lines,
    /// bright and bluish) — used comparatively, never as an absolute discriminator
    /// between fills and lines (wireframe lines are steel-colored too).</summary>
    private static int SteelPixels(byte[] rgba)
    {
        int count = 0;
        for (int p = 0; p < rgba.Length; p += 4)
        {
            if (rgba[p] > 80 && rgba[p + 2] > 110 && rgba[p + 2] >= rgba[p])
                count++;
        }
        return count;
    }

    /// <summary>Pixels where <paramref name="a"/> is darker than <paramref name="b"/>
    /// (green channel) by more than AA noise — how many pixels an edge overlay
    /// darkened. The threshold accounts for the 2x supersample: a 1px line covers one
    /// of four box-filter samples, so an edge only darkens the final pixel by about a
    /// quarter of the fill-to-line contrast (roughly 25-35 green levels on steel).</summary>
    private static int DarkenedPixels(byte[] a, byte[] b)
    {
        int count = 0;
        for (int p = 0; p < a.Length; p += 4)
        {
            if (b[p + 1] - a[p + 1] > 18)
                count++;
        }
        return count;
    }

    /// <summary>Warm pixels (red clearly above blue) — the cut-material tint; the
    /// steel part and the background are both bluish, so these only appear on
    /// section-exposed interiors.</summary>
    private static int WarmPixels(byte[] rgba)
    {
        int count = 0;
        for (int p = 0; p < rgba.Length; p += 4)
        {
            if (rgba[p] > rgba[p + 2] + 15)
                count++;
        }
        return count;
    }

    [SkippableFact]
    public void GlobalShaded_SuppressesFeatureEdges()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // A drilled bracket: the bore rims are feature edges in the fill's interior
        // (a plain box's edges all sit on the silhouette, where anti-aliased darkening
        // is half background already and the signal is weak).
        var scene = new Scene();
        scene.Add(new Part("bracket", Shape.Box(4, 3, 1) - Shape.Cylinder(0.5, 3)));
        scene.PreMesh();
        var parts = (IReadOnlyList<Part>)[.. scene.AllParts];
        var withEdges = Render(parts, ViewStyle.ShadedWithEdges);
        var without = Render(parts, ViewStyle.Shaded);

        // Same fill footprint; the only difference is the near-black edge overlay, so
        // with-edges darkens pixels along the box edges and brightens (almost) none.
        Assert.True(SteelPixels(without) > W * H / 20, "shaded render lost its fill");
        int darkened = DarkenedPixels(withEdges, without);
        int brightened = DarkenedPixels(without, withEdges);
        Assert.True(darkened > 300, $"expected a visible edge overlay, got {darkened} darkened pixels");
        Assert.True(brightened < darkened / 8,
            $"suppressing edges must not add pixels ({brightened} brightened vs {darkened} darkened)");
    }

    [SkippableFact]
    public void GlobalWireframe_DrawsLinesWithoutFills()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var parts = Box();
        int wire = SteelPixels(Render(parts, ViewStyle.Wireframe));
        int shaded = SteelPixels(Render(parts, ViewStyle.Shaded));

        // Lines are steel-colored too, so compare areas: a wireframe box is a handful
        // of 1px lines, a shaded box a large filled region.
        Assert.True(wire > 100, $"expected wireframe lines, got {wire} steel pixels");
        Assert.True(wire < shaded / 4, $"wireframe should not fill: {wire} vs shaded {shaded}");
    }

    [SkippableFact]
    public void GlobalPoints_DrawsSparseDots()
    {
        Skip.If(SkipReason is not null, SkipReason);
        int dots = SteelPixels(Render(Box(), ViewStyle.Points));
        int shaded = SteelPixels(Render(Box(), ViewStyle.Shaded));

        // Dots cover far less area than the fill but are not nothing (a box has 24
        // flat-shaded vertices; several face the camera).
        Assert.True(dots > 10, $"expected visible point sprites, got {dots} pixels");
        Assert.True(dots < shaded / 10, $"points view should be sparse: {dots} vs fill {shaded}");
    }

    [SkippableFact]
    public void PartDisplayMode_OverridesGlobalStyle()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // Same effective mode -> the exact same draw stream -> identical pixels: a part
        // explicitly set to Wireframe under global ShadedWithEdges renders exactly like
        // a default part under global Wireframe.
        var overridden = Render(Box(DisplayMode.Wireframe), ViewStyle.ShadedWithEdges);
        var globallyWireframed = Render(Box(), ViewStyle.Wireframe);
        Assert.Equal(globallyWireframed, overridden);

        // A translucent part ignores the global style entirely: identical under
        // Wireframe and ShadedWithEdges, and nothing like the default-part wireframe.
        var translucentUnderWire = Render(Box(DisplayMode.Translucent), ViewStyle.Wireframe);
        var translucentUnderEdges = Render(Box(DisplayMode.Translucent), ViewStyle.ShadedWithEdges);
        Assert.Equal(translucentUnderEdges, translucentUnderWire);
        int different = 0;
        for (int p = 0; p < translucentUnderWire.Length; p += 4)
        {
            if (Math.Abs(translucentUnderWire[p + 1] - globallyWireframed[p + 1]) > 20)
                different++;
        }
        Assert.True(different > 500,
            $"translucent override should not look like wireframe ({different} differing pixels)");
    }

    [SkippableFact]
    public void SectionZ_ClipsAndShowsCutMaterial()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var parts = Box();
        var whole = Render(parts);
        var sectioned = Render(parts, sectionOffset: 0.0);   // box spans z in [-1, 1]

        Assert.True(SteelPixels(sectioned) < SteelPixels(whole),
            "sectioning must remove fill pixels (the top half is clipped)");
        Assert.True(WarmPixels(whole) < 50, $"unsectioned render should have no cut tint, got {WarmPixels(whole)}");
        Assert.True(WarmPixels(sectioned) > 300,
            $"expected warm cut-material interior, got {WarmPixels(sectioned)} warm pixels");
    }

    [SkippableFact]
    public void SectionAxis_XAndY_ClipTheirOwnHalves()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var parts = Box();
        var whole = Render(parts);
        var cutX = Render(parts, axis: SectionAxis.X, sectionOffset: 0.0);   // box x in [-2, 2]
        var cutY = Render(parts, axis: SectionAxis.Y, sectionOffset: 0.0);   // box y in [-1.5, 1.5]

        Assert.True(SteelPixels(cutX) < SteelPixels(whole), "X-section should clip fill area");
        Assert.True(SteelPixels(cutY) < SteelPixels(whole), "Y-section should clip fill area");
        Assert.True(WarmPixels(cutX) > 300, $"X-section cut material missing ({WarmPixels(cutX)})");
        Assert.True(WarmPixels(cutY) > 300, $"Y-section cut material missing ({WarmPixels(cutY)})");

        // The two cuts remove different halves, so the images differ meaningfully.
        int different = 0;
        for (int p = 0; p < cutX.Length; p += 4)
        {
            if (Math.Abs(cutX[p] - cutY[p]) > 25 || Math.Abs(cutX[p + 2] - cutY[p + 2]) > 25)
                different++;
        }
        Assert.True(different > 500, $"X and Y sections look identical ({different} differing pixels)");
    }

    [SkippableFact]
    public void TranslucentPart_WithSection_RendersBoth()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // Opaque box beside a translucent one, both cut by a Z-section: the opaque
        // interior shows warm cut material while the translucent part still blends
        // (and is clipped too — its image half changes with the section).
        // Both parts pinned to Steel: the warm-pixel classifier must only ever see
        // cut material, and the palette's second auto-color (Coral) is warm.
        var scene = new Scene();
        scene.Add(new Part("solid", Shape.Box(2, 2, 2), Palette.Steel));
        var glass = scene.Add(new Part("glass", Shape.Box(2, 2, 2).Translate(3.5, 0, 0), Palette.Steel));
        glass.DisplayMode = DisplayMode.Translucent;
        scene.PreMesh();
        var parts = (IReadOnlyList<Part>)[.. scene.AllParts];

        var whole = Render(parts);
        var sectioned = Render(parts, sectionOffset: 0.0);
        Assert.True(WarmPixels(whole) < 50, $"no cut tint expected unsectioned, got {WarmPixels(whole)}");
        Assert.True(WarmPixels(sectioned) > 200,
            $"sectioned scene should expose warm cut material, got {WarmPixels(sectioned)}");
        Assert.True(SteelPixels(sectioned) < SteelPixels(whole),
            "the section should clip opaque fill area");

        // The translucent part must also be clipped: without translucent support the
        // sectioned/unsectioned pair would differ only on the opaque part; require
        // enough differing pixels that the glass half must have changed too.
        int different = 0;
        for (int p = 0; p < whole.Length; p += 4)
        {
            if (Math.Abs(whole[p + 1] - sectioned[p + 1]) > 15)
                different++;
        }
        Assert.True(different > SteelPixels(whole) - SteelPixels(sectioned) + 400,
            $"expected the translucent part to be sectioned as well ({different} differing pixels)");
    }

    [SkippableFact]
    public void EngrCadRenderToImage_AcceptsStyleAndSection()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var scene = new Scene();
        scene.Add(new Part("box", Shape.Box(2, 2, 2)));
        var path = Path.Combine(Path.GetTempPath(), $"engrcad-style-{Guid.NewGuid():N}.png");
        try
        {
            EngrCad.RenderToImage(scene, path, 160, 120,
                style: ViewStyle.Wireframe, sectionAxis: SectionAxis.X, sectionOffset: 0.0);
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
}
