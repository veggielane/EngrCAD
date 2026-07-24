using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Pixel-level verification that offscreen section renders draw the SDF isoline
/// overlay (the last offscreen-parity gap), including the axis-staleness regression:
/// the section AXIS must feed the contour build, so X and Y sections at numerically
/// equal offsets produce different isolines. The window path caches contour geometry
/// keyed on (axis, offset) — offscreen is one-shot, but this locks the same contract
/// through the shared <c>SectionContourRenderer</c>. Statistical assertions (pixel
/// classes and image diffs), not golden images. Shares the "offscreen-gl" collection
/// (no concurrent EGL contexts).
/// </summary>
[Collection("offscreen-gl")]
public class OffscreenIsolineTests
{
    private const int W = 320, H = 240;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>An asymmetric body (4 x 2 x 2, centered) as a Shape part — Shapes have
    /// an implicit lowering, so the section overlay draws isolines for it.</summary>
    private static IReadOnlyList<Part> ShapeParts()
    {
        var scene = new Scene();
        scene.Add(new Part("body", Shape.Box(4, 2, 2)));
        scene.PreMesh();
        return [.. scene.AllParts];
    }

    /// <summary>The same body as a raw mesh part: identical triangles and color, but
    /// no SDF route — the isoline-free twin of <see cref="ShapeParts"/>.</summary>
    private static IReadOnlyList<Part> MeshParts()
    {
        var scene = new Scene();
        scene.Add(new Part("body", Shape.Box(4, 2, 2).ToMesh()));
        scene.PreMesh();
        return [.. scene.AllParts];
    }

    private static byte[] Render(
        IReadOnlyList<Part> parts, SectionAxis axis = SectionAxis.Z, double? offset = null) =>
        OffscreenRenderer.Render(parts, W, H, camera: null, furniture: false,
            ViewStyle.ShadedWithEdges, axis, offset);

    private static int Different(byte[] a, byte[] b, int threshold = 20)
    {
        int count = 0;
        for (int p = 0; p < a.Length; p += 4)
        {
            if (Math.Abs(a[p] - b[p]) > threshold
                || Math.Abs(a[p + 1] - b[p + 1]) > threshold
                || Math.Abs(a[p + 2] - b[p + 2]) > threshold)
                count++;
        }
        return count;
    }

    /// <summary>Pixels leaning toward the zero-isoline gold (1.0, 0.90, 0.45). Lines
    /// are 1px in the 2x supersampled buffer, so a final pixel is at most ~50-75%
    /// gold blended with cut material or background; the thresholds accept that blend
    /// while rejecting steel fills (bluish), cut material (dim), the warm negative
    /// contour family (green too low), and specular highlights (r - b too small).</summary>
    private static int GoldPixels(byte[] rgba)
    {
        int count = 0;
        for (int p = 0; p < rgba.Length; p += 4)
        {
            if (rgba[p] > 180 && rgba[p + 1] > 150 && rgba[p + 2] < 150
                && rgba[p] - rgba[p + 2] > 60)
                count++;
        }
        return count;
    }

    [SkippableFact]
    public void SectionedSdfPart_DrawsIsolines_MeshTwinDoesNot()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var shape = ShapeParts();
        var mesh = MeshParts();

        // Unsectioned, the two scenes render the same box with the same 12 edge
        // segments — the isoline machinery must not touch an unsectioned render.
        // Not byte-identical: the shape part's overlay comes from the B-Rep edges,
        // the mesh part's from mesh dihedrals, and although the segments coincide
        // geometrically their direction/order differs, which GL line rasterization
        // (diamond-exit endpoints) can shift by a pixel. So: no gold anywhere, and
        // only a rasterization-noise level of differing pixels.
        Assert.Equal(0, GoldPixels(Render(shape)));
        Assert.Equal(0, GoldPixels(Render(mesh)));
        Assert.True(Different(Render(shape), Render(mesh)) < 50,
            "unsectioned twins should differ only by line-endpoint rasterization noise");

        // Sectioned, only the SDF-routed part gains the overlay: gold zero-contour
        // pixels appear, and the images now differ exactly by the isolines.
        var sectionedShape = Render(shape, SectionAxis.Z, 0.0);   // box spans z [-1, 1]
        var sectionedMesh = Render(mesh, SectionAxis.Z, 0.0);
        Assert.True(GoldPixels(sectionedShape) > 30,
            $"expected gold zero-contour pixels, got {GoldPixels(sectionedShape)}");
        Assert.True(GoldPixels(sectionedMesh) < 5,
            $"mesh part must not get isolines, got {GoldPixels(sectionedMesh)} gold pixels");
        Assert.True(Different(sectionedShape, sectionedMesh) > 100,
            "the isoline overlay should change a visible number of pixels");
    }

    [SkippableFact]
    public void SectionAxis_XvsY_EqualOffsets_ProduceDifferentIsolines()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // The regression scenario: switching the axis while the offset stays
        // numerically identical (0 for an origin-centered part) must change the
        // section AND its isolines — the axis is part of the contour-build key.
        var shape = ShapeParts();
        var cutX = Render(shape, SectionAxis.X, 0.0);
        var cutY = Render(shape, SectionAxis.Y, 0.0);

        Assert.True(GoldPixels(cutX) > 30, $"X section lost its isolines ({GoldPixels(cutX)} gold)");
        Assert.True(GoldPixels(cutY) > 30, $"Y section lost its isolines ({GoldPixels(cutY)} gold)");
        Assert.True(Different(cutX, cutY) > 500,
            $"X and Y sections at equal offsets look identical ({Different(cutX, cutY)} differing pixels)");

        // Each axis's isolines differ from its isoline-free mesh twin — the overlay
        // followed the plane on both axes, not just the default Z.
        var mesh = MeshParts();
        Assert.True(Different(cutX, Render(mesh, SectionAxis.X, 0.0)) > 100,
            "X-section isolines missing against the mesh twin");
        Assert.True(Different(cutY, Render(mesh, SectionAxis.Y, 0.0)) > 100,
            "Y-section isolines missing against the mesh twin");
    }
}
