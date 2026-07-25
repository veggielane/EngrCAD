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

    /// <summary>
    /// Face-on at the y = 0 plane (eye on +Y, screen up = +Z, screen right = -X), so
    /// that plane's isolines project true-shape and their vertical extent maps directly
    /// to world z. Distance 8 with the 45-degree vertical FOV puts the 4 x 2 x 2 box's
    /// z = +/-1 faces about 36 rows either side of the centre row.
    /// </summary>
    private static readonly CameraState FaceOnY = new(Math.PI / 2, 0, 8, (0, 0, 0));

    private static byte[] RenderPlanes(
        IReadOnlyList<Part> parts, IReadOnlyList<SectionPlane> planes,
        SectionCombine combine = SectionCombine.Intersection) =>
        OffscreenRenderer.Render(parts, W, H, FaceOnY, furniture: false,
            ViewStyle.ShadedWithEdges, SectionAxis.Z, sectionOffset: null,
            ambientOcclusion: false, planes, combine);

    /// <summary>
    /// Contour pixels of the two WARM families — the gold zero contour (1.0, 0.90, 0.45)
    /// and the negative inside-material family (0.92, 0.55, 0.32) — in rows
    /// [<paramref name="from"/>, <paramref name="to"/>). Both families live ON the
    /// section plane, which is what this rule is about; the cool positive family is
    /// deliberately excluded because a lit fill can be bluish too. Measured references
    /// for the thresholds (default steel part, this camera): cut material is
    /// (124, 104, 95) so r - b = 29, lit steel is (92, 114, 141) so r - b is negative,
    /// background (44, 49, 59) likewise. Even a 40% blend of the orange family with the
    /// cut material lands at r - b near 78, so both thresholds keep real margin.
    /// </summary>
    private static int WarmContourPixels(byte[] rgba, int from, int to)
    {
        int count = 0;
        for (int row = from; row < to; row++)
        {
            for (int x = 0; x < W; x++)
            {
                int p = (row * W + x) * 4;
                if (rgba[p] - rgba[p + 2] > 45 && rgba[p] > 130)
                    count++;
            }
        }
        return count;
    }

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

    // ---- multi-plane cuts: each plane's isolines stay on its own exposed cut face ----

    // The centre row +/- a small guard: the OTHER plane of these two-plane cuts is
    // edge-on to this camera, so its own contours land on the centre row and must not be
    // counted for either half.
    private const int UpperEnd = H / 2 - 6;     // rows [0, 114) are strictly z > 0
    private const int LowerStart = H / 2 + 6;   // rows [126, 240) are strictly z < 0

    // Bands strictly OUTSIDE the body's silhouette (the 4 x 2 x 2 box covers rows ~85 to
    // ~156 at this camera): the only thing that can appear there is the padded positive
    // contour family, with nothing to occlude it. This is where a missing sibling clip
    // shows up under Intersection — inside the silhouette the buried half of the cut
    // face is hidden by the solid material in front of it anyway, so the "contour fans
    // into empty space beyond the part" symptom is the honest detector.
    private const int AboveBodyStart = 35, AboveBodyEnd = 78;
    private const int BelowBodyStart = 162, BelowBodyEnd = 205;

    /// <summary>Pixels of the COOL positive (outside-the-body) contour family. The dark
    /// background is (44, 49, 59) and the cut material is warm, so a blue-leaning bright
    /// pixel outside the silhouette can only be a positive contour.</summary>
    private static int CoolContourPixels(byte[] rgba, int from, int to)
    {
        int count = 0;
        for (int row = from; row < to; row++)
        {
            for (int x = 0; x < W; x++)
            {
                int p = (row * W + x) * 4;
                if (rgba[p + 2] - rgba[p] > 30 && rgba[p + 2] > 75)
                    count++;
            }
        }
        return count;
    }

    // A leaked half of the cut face is hundreds of pixels (the reference render measures
    // ~750 warm per half and ~500 cool per outside band), so anything under this is
    // rasterization jitter at the guard band.
    private const int Leak = 20;

    [SkippableFact]
    public void QuarterCut_IsolinesOnlyCoverEachPlanesExposedCutFace()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var shape = ShapeParts();

        // Reference: with the y = 0 plane alone the whole cross-section is exposed, so
        // its contours appear above AND below z = 0 (symmetric box, symmetric counts).
        var single = RenderPlanes(shape, [SectionPlane.On(SectionAxis.Y, 0)]);
        int singleUpper = WarmContourPixels(single, 0, UpperEnd);
        int singleLower = WarmContourPixels(single, LowerStart, H);
        Assert.True(singleUpper > 200, $"single-plane isolines missing above z = 0 ({singleUpper})");
        Assert.True(singleLower > 200, $"single-plane isolines missing below z = 0 ({singleLower})");
        Assert.True(CoolContourPixels(single, AboveBodyStart, AboveBodyEnd) > 200);
        Assert.True(CoolContourPixels(single, BelowBodyStart, BelowBodyEnd) > 200);

        // Quarter cut z > 0 AND y > 0: the y = 0 face is exposed only where the z plane
        // also excludes, i.e. z > 0. Below the z plane that face is buried in solid
        // material, and its contours must be gone.
        var quarter = RenderPlanes(shape,
            [SectionPlane.On(SectionAxis.Z, 0), SectionPlane.On(SectionAxis.Y, 0)]);
        int exposed = WarmContourPixels(quarter, 0, UpperEnd);
        int buried = WarmContourPixels(quarter, LowerStart, H);
        Assert.True(exposed > 200, $"the exposed half of the cut face lost its isolines ({exposed})");
        Assert.True(buried < Leak,
            $"isolines drawn on the half of the cut face buried in material ({buried} px)");

        // The reported symptom, asserted directly: the positive family still fans out
        // past the silhouette on the EXPOSED side and must be gone on the buried one.
        int fanAbove = CoolContourPixels(quarter, AboveBodyStart, AboveBodyEnd);
        int fanBelow = CoolContourPixels(quarter, BelowBodyStart, BelowBodyEnd);
        Assert.True(fanAbove > 200, $"the exposed side lost its outside-the-body levels ({fanAbove})");
        Assert.True(fanBelow < Leak, $"contour fans reach into the buried side ({fanBelow} px)");
    }

    [SkippableFact]
    public void UnionCut_IsolinesCoverTheOppositeHalfOfTheQuarterCut()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var shape = ShapeParts();

        // Union keeps only what EVERY plane keeps (z < 0 and y < 0), so the y = 0 face
        // is exposed on the opposite half from the quarter cut. Getting this backwards
        // is the exact failure the sibling rule exists to prevent, and it is invisible
        // in any single-plane render.
        var union = RenderPlanes(shape,
            [SectionPlane.On(SectionAxis.Z, 0), SectionPlane.On(SectionAxis.Y, 0)],
            SectionCombine.Union);
        int removed = WarmContourPixels(union, 0, UpperEnd);
        int kept = WarmContourPixels(union, LowerStart, H);
        Assert.True(removed < Leak, $"isolines drawn over the removed half ({removed} px)");
        Assert.True(kept > 200, $"the kept quadrant's cut face lost its isolines ({kept})");
        Assert.True(CoolContourPixels(union, AboveBodyStart, AboveBodyEnd) < Leak);
        Assert.True(CoolContourPixels(union, BelowBodyStart, BelowBodyEnd) > 200);
    }
}
