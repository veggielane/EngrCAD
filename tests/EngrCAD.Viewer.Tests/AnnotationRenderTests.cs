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
