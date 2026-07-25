using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Headless construction-tree previews: selecting a build step in the model tree draws
/// its edges over the model, and a <c>RenderToImage</c> must be able to do the same —
/// the parity rule the isolines and annotations already follow. Both paths go through
/// the one <see cref="PreviewLayer"/>, so the colour, the always-on-top depth rule and
/// the never-section-clipped rule cannot drift. Statistical pixel assertions, not golden
/// images. Shares the "offscreen-gl" collection (no concurrent EGL contexts).
/// </summary>
[Collection("offscreen-gl")]
public class OffscreenPreviewTests
{
    private const int W = 320, H = 240;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>A drilled plate: the construction tree is "difference" over a box and a
    /// cylinder, so an operand row previews geometry the finished part does not show.</summary>
    private static (Scene Scene, Part Part) Drilled()
    {
        var scene = new Scene(new MeshQuality { SegmentsPerCircle = 24, CurveSamples = 12 });
        var part = scene.Add(new Part("plate", Shape.Box(6, 4, 1.5) - Shape.Cylinder(1, 4)));
        scene.PreMesh();
        return (scene, part);
    }

    /// <summary>Pixels of the preview's construction cyan (0.35, 0.92, 1.0) — nothing
    /// else in the render is that blue-green: part fills are steel, feature edges near
    /// black, the background dark navy.</summary>
    private static int CyanPixels(byte[] rgba)
    {
        int count = 0;
        for (int p = 0; p < rgba.Length; p += 4)
        {
            if (rgba[p + 2] > 140 && rgba[p + 1] > 120 && rgba[p + 2] - rgba[p] > 60
                && rgba[p + 1] - rgba[p] > 40)
                count++;
        }
        return count;
    }

    private static byte[] Render(Scene scene, ConstructionPreviewRequest? preview)
    {
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-preview-{Guid.NewGuid():N}.png");
        try
        {
            EngrCad.RenderToImage(scene, path, W, H, camera: null,
                ViewStyle.ShadedWithEdges, preview: preview);
            return File.ReadAllBytes(path);   // only used to prove the file was written
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static byte[] Pixels(Scene scene, ConstructionPreviewRequest? preview)
    {
        var instances = scene.AllInstances.ToList();
        var (segments, world) = preview is { } request
            ? request.Build(instances, scene.ResolveQuality())
            : (null, Matrix4d.Identity);
        return OffscreenRenderer.Render(instances, W, H, camera: null, furniture: false,
            ViewStyle.ShadedWithEdges, SectionAxis.Z, sectionOffset: null,
            ambientOcclusion: false, sectionPlanes: null,
            sectionCombine: SectionCombine.Intersection, preview: segments, previewWorld: world);
    }

    [SkippableFact]
    public void RequestingARow_DrawsItsEdgesOverTheModel()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var (scene, part) = Drilled();
        var root = part.ConstructionTree();
        Assert.NotNull(root);

        var without = Pixels(scene, null);
        Assert.Equal(0, CyanPixels(without));

        var with = Pixels(scene, new ConstructionPreviewRequest(part, root));
        Assert.True(CyanPixels(with) > 50,
            $"the preview overlay did not draw ({CyanPixels(with)} construction-cyan pixels)");
    }

    [SkippableFact]
    public void AnOperandRow_PreviewsGeometryTheFinishedPartDoesNotShow()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var (scene, part) = Drilled();
        var root = part.ConstructionTree()!;
        Assert.NotEmpty(root.Children);

        // The subtracted tool: a full cylinder, most of which is buried in the plate.
        // Its preview must still be visible, because previews are always-on-top (the
        // rollback view is an inspection aid, not model geometry).
        var tool = root.Children[^1];
        var whole = Pixels(scene, new ConstructionPreviewRequest(part, root));
        var operand = Pixels(scene, new ConstructionPreviewRequest(part, tool));

        Assert.True(CyanPixels(operand) > 50,
            $"the operand preview did not draw ({CyanPixels(operand)} cyan pixels)");
        Assert.NotEqual(CyanPixels(whole), CyanPixels(operand));
    }

    [SkippableFact]
    public void ARowThatCannotBePreviewed_FailsLoudly()
    {
        Skip.If(SkipReason is not null, SkipReason);
        // A [Param] row under a feature carries neither a shape target nor a sketch, so
        // it has nothing to draw. Rendering a silently empty overlay would let a docs
        // page claim a preview it never made.
        var history = new FeatureHistory();
        history.Add(new PlateFeature());
        var scene = new Scene();
        var part = scene.Add(history.ToPart("plate"));
        scene.PreMesh();

        var parameter = part.ConstructionTree()!.Flatten()
            .First(n => n.Kind == ConstructionNodeKind.Parameter);
        Assert.False(parameter.CanPreview);
        var error = Assert.Throws<InvalidOperationException>(
            () => Pixels(scene, new ConstructionPreviewRequest(part, parameter)));
        Assert.Contains("no geometry to preview", error.Message);
    }

    private sealed class PlateFeature : Feature
    {
        [Param(Min = 1, Max = 20, Units = "mm")]
        public double Size { get; init; } = 6;

        public override Shape Apply(FeatureContext context) => Shape.Box(Size, Size, 2);
    }

    [SkippableFact]
    public void TheFrontDoorWritesAPngWithThePreview()
    {
        Skip.If(SkipReason is not null, SkipReason);
        var (scene, part) = Drilled();
        var bytes = Render(scene, new ConstructionPreviewRequest(part, part.ConstructionTree()!));
        Assert.True(bytes.Length > 0);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
    }
}
