using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Pixel-level verification that the offscreen pass draws annotations — dimensions
/// are documentation, so headless/docs renders must carry them (unlike the
/// interactive view cube, which is deliberately window-only). Statistical assertions
/// (differential pixel counts), not golden images. GL-touching test classes share the
/// "offscreen-gl" collection so EGL contexts are never created concurrently.
/// </summary>
[Collection("offscreen-gl")]
public class AnnotationRenderTests
{
    private const int W = 320, H = 240;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>A plate, optionally dimensioned: width dimension pulled above the
    /// part plus a leader note (the classic annotated-part look).</summary>
    private static IReadOnlyList<Part> Plate(bool annotated)
    {
        var scene = new Scene();
        var part = scene.Add(new Part("plate", Shape.Box(40, 20, 5)));
        if (annotated)
        {
            part.Annotate(LinearDimension.BetweenFaces(
                s => s.PlanarFacesWithNormal(-Vector3d.UnitX).First(),
                s => s.PlanarFacesWithNormal(Vector3d.UnitX).First()));
            part.Annotate(new LeaderNote((10, -10, 2.5), "PLATE"));
        }
        scene.PreMesh();
        return [.. scene.AllParts];
    }

    /// <summary>Pixels differing clearly between two renders (channel sum).</summary>
    private static int ChangedPixels(byte[] a, byte[] b, Func<int, bool>? where = null)
    {
        int count = 0;
        for (int p = 0; p < a.Length; p += 4)
        {
            int delta = Math.Abs(a[p] - b[p]) + Math.Abs(a[p + 1] - b[p + 1]) + Math.Abs(a[p + 2] - b[p + 2]);
            if (delta > 40 && (where is null || where(p)))
                count++;
        }
        return count;
    }

    [SkippableFact]
    public void OffscreenRender_DrawsAnnotations()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var bare = OffscreenRenderer.Render(Plate(false), W, H, furniture: false);
        var annotated = OffscreenRenderer.Render(Plate(true), W, H, furniture: false);

        // The annotation overlay adds bright line pixels: dimension/extension lines,
        // arrowheads, and the "40"/"PLATE" stroke text.
        int changed = ChangedPixels(annotated, bare);
        Assert.True(changed > 150, $"expected visible annotation lines, got {changed} changed pixels");

        // Some of them lie OUTSIDE the part's silhouette (the dimension line is
        // pulled off the model; the leader ends in empty space): pixels that were
        // pure background in the bare render.
        int onBackground = ChangedPixels(annotated, bare,
            p => bare[p] < 70 && bare[p + 1] < 75 && bare[p + 2] < 85);
        Assert.True(onBackground > 60,
            $"expected dimension/leader lines off the part, got {onBackground} background pixels changed");
    }

    [SkippableFact]
    public void OffscreenRender_SkipsUnresolvableAnnotations()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // A mesh part with a selector dimension: resolution fails, the render must
        // still succeed and just draw the bare part.
        var scene = new Scene();
        scene.Add(new Part("meshpart", Shape.Box(40, 20, 5).ToMesh())
            .Annotate(LinearDimension.BetweenFaces(
                s => s.PlanarFacesWithNormal(Vector3d.UnitZ).First(),
                s => s.PlanarFacesWithNormal(-Vector3d.UnitZ).First())));
        scene.PreMesh();
        var parts = (IReadOnlyList<Part>)[.. scene.AllParts];

        var pixels = OffscreenRenderer.Render(parts, W, H, furniture: false);
        var bare = OffscreenRenderer.Render(Plate(false), W, H, furniture: false);
        Assert.Equal(0, ChangedPixels(pixels, bare));   // identical to the bare plate
    }

    // ---- occlusion-aware rendering (AnnotationDepth.Occluded) ----

    /// <summary>
    /// A scene with no annotations must render BYTE-identically under either mode: the
    /// depth-function machinery lives entirely inside the overlay pass, so a model that
    /// carries no dimensions cannot notice it exists. This is the guard that stops the
    /// mode leaking into the 130-odd committed docs renders.
    /// </summary>
    [SkippableFact]
    public void UnannotatedScenesRenderIdenticallyInBothModes()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var top = OffscreenRenderer.Render(Plate(false), W, H);
        var occluded = OffscreenRenderer.Render(Plate(false), W, H,
            annotationDepth: AnnotationDepth.Occluded);
        Assert.Equal(top, occluded);
    }

    /// <summary>
    /// The mode's whole claim, as pixels: an annotation stretch with material in front of
    /// it is drawn DARKER, and one over empty space is untouched.
    /// <para>The fixture puts a dimension at mid-thickness inside a plate — the default
    /// placement, so this is the ordinary case rather than a contrived one — and the
    /// assertion is directional: every pixel the two modes disagree about must have got
    /// darker, never lighter. A count alone would pass an implementation that dimmed the
    /// wrong half, and a "looks right" screenshot would pass one that dimmed everything.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void OccludedDimsTheStretchesBehindTheModelAndNothingElse()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var top = OffscreenRenderer.Render(Plate(true), W, H, furniture: false);
        var occluded = OffscreenRenderer.Render(Plate(true), W, H, furniture: false,
            annotationDepth: AnnotationDepth.Occluded);

        int darker = 0, lighter = 0;
        for (int p = 0; p < top.Length; p += 4)
        {
            int delta = (occluded[p] - top[p]) + (occluded[p + 1] - top[p + 1])
                + (occluded[p + 2] - top[p + 2]);
            if (delta < -40) darker++;
            else if (delta > 40) lighter++;
        }
        Assert.True(darker > 40, $"expected hidden stretches to dim, got {darker} darker pixels");
        // EXACTLY none the other way. The mode is a colour change and nothing else: the
        // depth bias moves each point along its own eye ray, so no anti-aliased line
        // coverage is redistributed and no pixel can brighten. A bound like
        // "lighter < darker / 4" would have accepted the first, ray-breaking
        // implementation (105 lighter against 308 darker); zero does not.
        Assert.Equal(0, lighter);
    }

    /// <summary>
    /// An annotation in free space is unaffected: with nothing in front of it, LEQUAL
    /// takes every fragment and GREATER takes none, so the occluded render is the
    /// always-on-top one.
    /// <para>This is the half that proves the depth test is being READ rather than the
    /// colour simply being swapped — an implementation that dimmed unconditionally would
    /// pass the test above and fail this one.</para>
    /// </summary>
    [SkippableFact]
    public void OccludedLeavesAnnotationsInFreeSpaceAlone()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var scene = new Scene();
        // The note's anchor is well clear of the plate and its leader runs outward into
        // empty space, so no part of the overlay has material in front of it.
        scene.Add(new Part("plate", Shape.Box(10, 10, 4))
            .Annotate(new LeaderNote((0, -40, 20), "CLEAR")));
        scene.PreMesh();
        var parts = (IReadOnlyList<Part>)[.. scene.AllParts];

        var camera = new CameraState(0.7, 0.45, 120, (0, 0, 0));
        var top = OffscreenRenderer.Render(parts, W, H, camera, furniture: false);
        var occluded = OffscreenRenderer.Render(parts, W, H, camera, furniture: false,
            annotationDepth: AnnotationDepth.Occluded);

        // Byte-identical, not merely close: with no occluder the LEQUAL pass draws
        // exactly what the depth-off pass drew, and the ray-preserving bias leaves the
        // screen position alone, so there is nothing left to differ by.
        Assert.Equal(top, occluded);
    }

    [SkippableFact]
    public void OffscreenRender_PosesAnnotationsWithTheInstanceTransform()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // The same annotated part rendered translated: the annotation pixels move
        // with the part (differential against the untranslated render is nonzero in
        // both, and the annotated regions differ).
        static IReadOnlyList<Part> At(double x)
        {
            var scene = new Scene();
            var part = scene.Add(new Part("plate", Shape.Box(10, 10, 4))
            {
                Transform = Matrix4d.CreateTranslation((x, 0, 0)),
            });
            part.Annotate(new LeaderNote((0, 0, 2), "N"));
            scene.PreMesh();
            return [.. scene.AllParts];
        }

        var camera = new CameraState(0.7, 0.45, 60, (0, 0, 0));
        var centered = OffscreenRenderer.Render(At(0), W, H, camera, furniture: false);
        var moved = OffscreenRenderer.Render(At(15), W, H, camera, furniture: false);
        Assert.True(ChangedPixels(centered, moved) > 100, "translated instance must move its annotation");
    }
}
