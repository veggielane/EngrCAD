using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The verification bar for animating a structural result: <b>an animated frame at t must
/// equal a static render of the same configuration, byte for byte.</b>
/// <para>That equality is what makes the whole design honest. The displacement rides as a
/// vertex attribute and a deformation track changes one float uniform per frame, so
/// nothing about a frame is approximated or interpolated: the frame at factor f IS the
/// render of the model displaced by f. If the two ever disagreed, the cheap animation
/// path and the static path would have become two renderers.</para>
/// </summary>
[Collection("offscreen-gl")]
public class DeformationAnimationTests
{
    private const int W = 320, H = 260;

    private static string? SkipReason =>
        OffscreenRenderer.IsAvailable ? null
        : $"no offscreen GL context on this machine: {OffscreenRenderer.UnavailableReason}";

    /// <summary>A cantilever plate with an analytic deflection — the docs' fixture, kept
    /// small. <paramref name="scale"/> is the display's own exaggeration.</summary>
    private static Scene Cantilever(double scale)
    {
        var scene = new Scene();
        var part = new Part("plate", Shape.Box(60, 12, 3), Palette.Steel);
        scene.Add(part);
        scene.PreMesh();
        var mesh = part.GetMesh();
        part.AddResult(MeshField.SampleVector(mesh, "u", "mm",
            p => new Vector3d(0, 0, 0.004 * (p.X + 30) * (p.X + 30))));
        part.FieldDisplay = new FieldDisplay
        {
            Field = "u",
            Deform = "u",
            DeformScale = scale,
        };
        return scene;
    }

    private static CameraState Camera(Scene scene)
    {
        var bounds = Aabb.Empty;
        foreach (var instance in scene.Instances())
            bounds = bounds.Union(instance.Bounds());
        return CameraMath.DefaultCamera(bounds);
    }

    [SkippableTheory]
    [InlineData(0.35)]
    [InlineData(1.0)]
    [InlineData(2.5)]
    public void AnAnimatedFrame_IsByteIdenticalToAStaticRenderOfTheSameScale(double factor)
    {
        Skip.If(SkipReason is not null, SkipReason);

        // The animated route: a part at 25x driven by a deformation track to `factor`.
        const double stated = 25;
        var animatedScene = Cantilever(stated);
        var animation = new Animation(2).With(DeformationTracks.Constant(factor));
        var camera = Camera(animatedScene);
        string path = Path.Combine(Path.GetTempPath(), $"engrcad-deform-{Guid.NewGuid():N}.png");
        try
        {
            EngrCad.RenderToImage(animatedScene, animation, t: 0.5, path, W, H, camera,
                ambientOcclusion: false);
            var animated = File.ReadAllBytes(path);

            // The static route: the SAME model displayed at the product outright.
            var stillScene = Cantilever(stated * factor);
            string stillPath = Path.Combine(Path.GetTempPath(), $"engrcad-still-{Guid.NewGuid():N}.png");
            try
            {
                EngrCad.RenderToImage(stillScene, stillPath, W, H, camera, ambientOcclusion: false);
                Assert.Equal(
                    Convert.ToHexString(File.ReadAllBytes(stillPath)),
                    Convert.ToHexString(animated));
            }
            finally
            {
                File.Delete(stillPath);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SkippableFact]
    public void ADeformationTrack_ChangesTheImageWithoutChangingTheInstances()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // The animation contract, restated for the deformation track: the instance list
        // is independent of t (that is what lets one upload serve a whole clip), and yet
        // the pixels move — because the change rides a uniform.
        var scene = Cantilever(25);
        var animation = new Animation(2).With(DeformationTracks.LoadRamp());
        var at0 = EngrCad.PoseAt(scene, animation, 0);
        var atHalf = EngrCad.PoseAt(scene, animation, 0.5);
        Assert.Equal(at0.Count, atHalf.Count);
        for (int i = 0; i < at0.Count; i++)
        {
            Assert.Same(at0[i].Part, atHalf[i].Part);
            Assert.Equal(at0[i].World, atHalf[i].World);
        }

        var camera = Camera(scene);
        var timeline = new List<(IReadOnlyList<PartInstance> Instances, CameraState Camera, double Factor)>();
        for (int i = 0; i < 4; i++)
        {
            double t = i / 4.0;
            timeline.Add((EngrCad.PoseAt(scene, animation, t), camera, animation.At(t).DeformFactor));
        }
        var frames = OffscreenRenderer.RenderSequence(
            timeline, W, H, furniture: false, ambientOcclusion: false);
        // Frame 0 is the undeformed shape (ramp at 0) and frame 2 the peak; they must
        // differ, or the uniform is not reaching the shader.
        Assert.NotEqual(Convert.ToHexString(frames[0]), Convert.ToHexString(frames[2]));
        // And the ramp is symmetric, so frames 1 and 3 are the same configuration —
        // which also proves the batched export reuses one upload across factors.
        Assert.Equal(Convert.ToHexString(frames[1]), Convert.ToHexString(frames[3]));
    }

    [SkippableFact]
    public void APartWithNoDisplacement_RendersIdenticallyAtEveryDeformFactor()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // The constant-when-absent contract, at the pixel level: a mesh with no
        // displacement buffer reads zero offsets, so the uniform cannot move it. This is
        // the property the committed docs PNGs rely on.
        var scene = new Scene();
        var part = new Part("plate", Shape.Box(30, 20, 6), Palette.Steel);
        scene.Add(part);
        scene.PreMesh();
        var camera = Camera(scene);
        var instances = scene.Instances().ToList();

        var neutral = OffscreenRenderer.Render(instances, W, H, camera, furniture: true,
            ambientOcclusion: false);
        foreach (double factor in new[] { 0.0, 1.0, 250.0, -40.0 })
        {
            var moved = OffscreenRenderer.Render(instances, W, H, camera, furniture: true,
                ViewStyle.ShadedWithEdges, SectionAxis.Z, sectionOffset: null,
                ambientOcclusion: false, sectionPlanes: null,
                sectionCombine: SectionCombine.Intersection, preview: null, previewWorld: null,
                fields: true, deformFactor: factor);
            Assert.Equal(Convert.ToHexString(neutral), Convert.ToHexString(moved));
        }
    }

    [SkippableFact]
    public void AFrameAtFactorZero_IsNotTheSameAsAStillOfAnUndeformedDisplay()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // The consequence of keeping the DRAW LIST independent of t, stated as a test
        // rather than left to be discovered: a part that CARRIES a displacement draws its
        // ghost at every factor including zero — deciding that per frame would make an
        // animation re-upload, which is the one thing this design does not do. (The edge
        // overlay used to be part of this difference and no longer is: it is drawn now,
        // carrying its own displacement attribute, so at factor 0 it sits exactly on the
        // undeformed rims.) A display whose own scale is 0 draws no ghost, which keeps
        // the two pictures genuinely different.
        var camera = Camera(Cantilever(25));
        var animated = OffscreenRenderer.Render(
            [.. Cantilever(25).Instances()], W, H, camera, furniture: false,
            ViewStyle.ShadedWithEdges, SectionAxis.Z, sectionOffset: null,
            ambientOcclusion: false, sectionPlanes: null,
            sectionCombine: SectionCombine.Intersection, preview: null, previewWorld: null,
            fields: true, deformFactor: 0);
        var undeformedDisplay = OffscreenRenderer.Render(
            [.. Cantilever(0).Instances()], W, H, camera, furniture: false, ambientOcclusion: false);
        Assert.NotEqual(Convert.ToHexString(undeformedDisplay), Convert.ToHexString(animated));
    }

    [SkippableFact]
    public void TheLegendReportsTheEffectiveExaggeration()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // A legend saying "25X DEFORMED" over a frame drawn at 12.5X would be exactly the
        // lie its title exists to prevent, so the widget follows the factor.
        //
        // The fixture isolates it: a displacement field that is identically ZERO with a
        // non-zero stated scale. Every draw is then factor-independent — the body never
        // moves, the edge overlay's offsets are zero so it never moves either, and the
        // ghost is the same at every factor — so ANY pixel difference between two factors
        // can only be the legend's title, which is stroke geometry and changes when the
        // number does.
        var scene = new Scene();
        var part = new Part("plate", Shape.Box(60, 12, 3), Palette.Steel);
        scene.Add(part);
        scene.PreMesh();
        var mesh = part.GetMesh();
        part.AddResult(MeshField.Sample(mesh, "stress", "MPa", p => p.X));
        part.AddResult(MeshField.SampleVector(mesh, "u", "mm", _ => Vector3d.Zero));
        part.FieldDisplay = new FieldDisplay { Field = "stress", Deform = "u", DeformScale = 25 };

        var camera = Camera(scene);
        var instances = scene.Instances().ToList();
        var full = OffscreenRenderer.Render(instances, W, H, camera, furniture: false,
            ambientOcclusion: false);
        var half = OffscreenRenderer.Render(instances, W, H, camera, furniture: false,
            ViewStyle.ShadedWithEdges, SectionAxis.Z, sectionOffset: null,
            ambientOcclusion: false, sectionPlanes: null,
            sectionCombine: SectionCombine.Intersection, preview: null, previewWorld: null,
            fields: true, deformFactor: 0.5);

        int changed = 0;
        for (int p = 0; p < full.Length; p += 4)
        {
            if (full[p] != half[p] || full[p + 1] != half[p + 1] || full[p + 2] != half[p + 2])
                changed++;
        }
        Assert.True(changed > 20, $"the legend did not follow the factor ({changed} pixels changed)");
        // And the string itself, so a failure says which half is wrong.
        Assert.True(part.TryResolveFieldDisplay(out var display, out _));
        Assert.Contains("25X", FieldLegend.Title(FieldRendering.AtFactor(display, 1)!.Value));
        Assert.Contains("12.5X", FieldLegend.Title(FieldRendering.AtFactor(display, 0.5)!.Value));
    }

    [SkippableFact]
    public void ADeformedPartDrawsItsEdgeOverlay_AndTheOverlayFollowsTheFactor()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // THE regression for the retired rule: a deformed part's ShadedWithEdges render
        // used to be byte-identical to its Shaded render, because the overlay was
        // dropped at upload. The edges are drawn now — and they follow the displacement,
        // so the overlay PIXELS (where with-edges differs from shaded) move with the
        // factor rather than outlining a shape that is no longer there.
        var scene = Cantilever(25);
        var camera = Camera(scene);
        var instances = scene.Instances().ToList();

        byte[] At(ViewStyle style, double factor) => OffscreenRenderer.Render(
            instances, W, H, camera, furniture: false, style,
            SectionAxis.Z, sectionOffset: null, ambientOcclusion: false,
            sectionPlanes: null, sectionCombine: SectionCombine.Intersection,
            preview: null, previewWorld: null, fields: true, deformFactor: factor);

        var shaded1 = At(ViewStyle.Shaded, 1);
        var edges1 = At(ViewStyle.ShadedWithEdges, 1);
        Assert.NotEqual(Convert.ToHexString(shaded1), Convert.ToHexString(edges1));

        // The overlay's own pixel set at factor 0 vs factor 1: both non-empty, and
        // different sets — the outline moved with the shape.
        static List<int> Overlay(byte[] shaded, byte[] withEdges)
        {
            var pixels = new List<int>();
            for (int i = 0; i < shaded.Length; i += 4)
            {
                if (shaded[i] != withEdges[i] || shaded[i + 1] != withEdges[i + 1]
                    || shaded[i + 2] != withEdges[i + 2])
                    pixels.Add(i / 4);
            }
            return pixels;
        }

        var overlay0 = Overlay(At(ViewStyle.Shaded, 0), At(ViewStyle.ShadedWithEdges, 0));
        var overlay1 = Overlay(shaded1, edges1);
        Assert.NotEmpty(overlay0);
        Assert.NotEmpty(overlay1);
        Assert.NotEqual(overlay0, overlay1);
    }

    [SkippableFact]
    public void AWireframeOfADeformedPart_FollowsTheDisplacement()
    {
        Skip.If(SkipReason is not null, SkipReason);

        // The wireframe gap PREDATES the attribute path: WireframeEdges reads the source
        // half-edge mesh, so a deformed part in Wireframe always drew its undeformed
        // edges while its fills moved. Now the wire upload carries per-endpoint offsets:
        // the factor moves the wireframe (it used to be factor-independent), and at
        // factor 0 the render is byte-identical to a scale-0 twin's — the offsets
        // contribute exactly zero, the constant-when-absent rule met from the other side.
        Scene Wireframe(double scale)
        {
            var scene = Cantilever(scale);
            var part = scene.Tabs[0].Parts[0];
            part.DisplayMode = DisplayMode.Wireframe;
            part.FieldDisplay = part.FieldDisplay! with { ShowUndeformed = false };
            return scene;
        }

        var deformed = Wireframe(25);
        var camera = Camera(deformed);

        byte[] At(Scene scene, double factor) => OffscreenRenderer.Render(
            [.. scene.Instances()], W, H, camera, furniture: false, ViewStyle.ShadedWithEdges,
            SectionAxis.Z, sectionOffset: null, ambientOcclusion: false,
            sectionPlanes: null, sectionCombine: SectionCombine.Intersection,
            preview: null, previewWorld: null, fields: true, deformFactor: factor);

        var rest = At(deformed, 0);
        var moved = At(deformed, 1);
        Assert.NotEqual(Convert.ToHexString(rest), Convert.ToHexString(moved));
        Assert.Equal(Convert.ToHexString(At(Wireframe(0), 1)), Convert.ToHexString(rest));
    }
}
