using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// Pure-math tests for the annotation overlay: billboard pixel-to-world conversion,
/// screen-constant sizing, the classic dimension anatomy (extension lines, dimension
/// line, arrowheads, text), leaders, and datum boxes — no GL.
/// </summary>
public class AnnotationGeometryTests
{
    private static AnnotationCamera Camera(double distance = 30, double heightPx = 800) =>
        AnnotationCamera.From(
            new CameraState(0.7, 0.45, distance, Vector3d.Zero), orthographic: false, heightPx, 1.0);

    private static List<(Vector3d A, Vector3d B)> Build(
        ResolvedAnnotation annotation, in AnnotationCamera camera)
    {
        var segments = new List<(Vector3d A, Vector3d B)>();
        AnnotationGeometry.Build(segments, [new AnnotationItem(annotation, Matrix4d.Identity)], camera);
        return segments;
    }

    // ---- billboard math ----

    [Fact]
    public void WorldPerPixel_Perspective_ScalesWithDepth()
    {
        var camera = Camera();
        var near = camera.Eye + camera.Forward * 10;
        var far = camera.Eye + camera.Forward * 20;
        Assert.Equal(2 * camera.WorldPerPixel(near), camera.WorldPerPixel(far), 1e-12);
    }

    [Fact]
    public void WorldPerPixel_Orthographic_IsDepthIndependent()
    {
        var camera = AnnotationCamera.From(
            new CameraState(0.7, 0.45, 30, Vector3d.Zero), orthographic: true, 800, 1.0);
        var near = camera.Eye + camera.Forward * 5;
        var far = camera.Eye + camera.Forward * 50;
        Assert.Equal(camera.WorldPerPixel(near), camera.WorldPerPixel(far), 1e-12);
    }

    [Fact]
    public void CameraBasis_IsOrthonormal()
    {
        var camera = Camera();
        Assert.Equal(1.0, camera.Forward.Length, 1e-12);
        Assert.Equal(1.0, camera.Right.Length, 1e-12);
        Assert.Equal(1.0, camera.Up.Length, 1e-12);
        Assert.Equal(0.0, camera.Forward.Dot(camera.Right), 1e-12);
        Assert.Equal(0.0, camera.Forward.Dot(camera.Up), 1e-12);
        Assert.Equal(0.0, camera.Right.Dot(camera.Up), 1e-12);
    }

    [Fact]
    public void PixelScale_MultipliesStyleSizes()
    {
        var one = AnnotationCamera.From(new CameraState(0, 0, 20, Vector3d.Zero), false, 800, 1.0);
        var two = AnnotationCamera.From(new CameraState(0, 0, 20, Vector3d.Zero), false, 800, 2.0);
        var at = Vector3d.Zero;
        Assert.Equal(2 * one.PxToWorld(12, at), two.PxToWorld(12, at), 1e-12);
    }

    // ---- linear dimension anatomy ----

    private static ResolvedAnnotation Linear(Vector3d a, Vector3d b, Vector3d? offset = null)
    {
        var dimension = new LinearDimension(a, b);
        if (offset is { } o)
            dimension.Offset = o;
        return dimension.Resolve();
    }

    [Fact]
    public void LinearDimension_HasExtensionLinesDimensionLineArrowsAndText()
    {
        var camera = Camera();
        var segments = Build(Linear((0, 0, 0), (40, 0, 0), (0, 0, 10)), camera);

        // Anatomy: 2 extension lines + 1 dimension line + 2 arrowheads (2 segments
        // each) + the stroke text ("40") on top.
        var textOnly = new List<(Vector3d A, Vector3d B)>();
        StrokeFont.AppendText(textOnly, "40", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1);
        Assert.Equal(2 + 1 + 4 + textOnly.Count, segments.Count);

        // The dimension line runs between the offset anchors, parallel to the span.
        var dimensionLine = segments[2];
        Assert.Equal(new Vector3d(0, 0, 10), dimensionLine.A);
        Assert.Equal(new Vector3d(40, 0, 10), dimensionLine.B);
    }

    [Fact]
    public void LinearDimension_ExtensionLinesLeaveAGapAtTheModel()
    {
        var camera = Camera();
        var segments = Build(Linear((0, 0, 0), (40, 0, 0), (0, 0, 10)), camera);

        // The first extension line starts a small gap above the anchor and overshoots
        // slightly past the dimension line (classic dimension anatomy).
        var extensionA = segments[0];
        Assert.True(extensionA.A.Z > 0, "extension line must not touch the model point");
        Assert.True(extensionA.B.Z > 10, "extension line must overshoot the dimension line");
        Assert.Equal(0.0, extensionA.A.X, 1e-12);
    }

    [Fact]
    public void LinearDimension_ZeroOffset_GetsScreenSpaceDefault()
    {
        var camera = Camera();
        var a = new Vector3d(0, 0, 0);
        var b = new Vector3d(40, 0, 0);
        var segments = Build(Linear(a, b), camera);

        // The dimension line (index 2) must be pulled off the measured span by the
        // default screen offset, staying parallel to it.
        var line = segments[2];
        var offset = line.A - a;
        Assert.True(offset.Length > 0.1, "default offset must displace the dimension line");
        Assert.Equal(0.0, offset.Dot((b - a).Normalized()), 1e-9);
        Assert.Equal(1.0, (line.B - line.A).Normalized().Dot((b - a).Normalized()), 1e-9);
    }

    [Fact]
    public void TextIsScreenConstant_AcrossCameraDistances()
    {
        // Same annotation from two camera distances: every text stroke's length in
        // PIXELS (world length / world-per-pixel at the stroke) is identical — the
        // screen-constant sizing contract.
        var annotation = Linear((0, 0, 0), (40, 0, 0), (0, 0, 10));
        var nearCamera = Camera(distance: 60);
        var farCamera = Camera(distance: 120);
        var near = Build(annotation, nearCamera);
        var far = Build(annotation, farCamera);

        Assert.Equal(near.Count, far.Count);
        // Compare the trailing text strokes (model-anchored lines don't scale).
        var textOnly = new List<(Vector3d A, Vector3d B)>();
        StrokeFont.AppendText(textOnly, "40", Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1);
        for (int i = near.Count - textOnly.Count; i < near.Count; i++)
        {
            double nearPx = near[i].A.DistanceTo(near[i].B)
                / nearCamera.WorldPerPixel((near[i].A + near[i].B) * 0.5);
            double farPx = far[i].A.DistanceTo(far[i].B)
                / farCamera.WorldPerPixel((far[i].A + far[i].B) * 0.5);
            Assert.Equal(nearPx, farPx, 1e-4);
        }
    }

    // ---- radial, leader, datum ----

    [Fact]
    public void RadialDimension_ArrowTouchesTheCircle()
    {
        var camera = Camera();
        var solid = EngrCAD.Modeling.Shape.Cylinder(5, 10).ToBrep();
        var resolved = RadialDimension.OnEdge(
            s => s.Faces.SelectMany(f => f.Edges()).First(e => e.IsCircular(out _, out _, out _)))
            .Resolve(solid);
        var segments = Build(resolved, camera);

        Assert.True(segments.Count > 4);
        // At least three segments (two arrow wings + the leader) start at the
        // on-circle anchor.
        int touching = segments.Count(s =>
            s.A.DistanceTo(resolved.AnchorA) < 1e-9 || s.B.DistanceTo(resolved.AnchorA) < 1e-9);
        Assert.True(touching >= 3, $"expected arrow + leader at the anchor, got {touching}");
    }

    [Fact]
    public void DatumLabel_AddsBoxAroundTheText()
    {
        var camera = Camera();
        var note = new LeaderNote((0, 0, 0), "A").Resolve();
        var datum = new DatumLabel((0, 0, 0), "A").Resolve();

        var noteSegments = Build(note, camera);
        var datumSegments = Build(datum, camera);
        Assert.Equal(noteSegments.Count + 4, datumSegments.Count);
    }

    [Fact]
    public void InstanceTransform_PosesAnnotations()
    {
        // The same annotation drawn through a translated instance moves with it.
        var camera = Camera();
        var annotation = new LeaderNote((0, 0, 0), "A").Resolve();
        var atOrigin = new List<(Vector3d A, Vector3d B)>();
        var translated = new List<(Vector3d A, Vector3d B)>();
        AnnotationGeometry.Build(atOrigin, [new AnnotationItem(annotation, Matrix4d.Identity)], camera);
        AnnotationGeometry.Build(translated,
            [new AnnotationItem(annotation, Matrix4d.CreateTranslation((100, 0, 0)))], camera);

        Assert.Equal(atOrigin.Count, translated.Count);
        // The leader's anchor endpoint moved by exactly the translation.
        Assert.Equal(100.0, translated[2].A.X - atOrigin[2].A.X, 1e-6);
    }

    /// <summary>
    /// A 3D annotation is sized in SCREEN pixels and a drawing sheet's in PAPER
    /// millimetres, so the two cannot share a constant — but they must share the
    /// PROPORTIONS, or the same dimension would look like a different product in the
    /// viewport and on the sheet.
    ///
    /// <para><c>SheetStyle</c> therefore holds each length as a ratio to its text
    /// height, and this asserts those ratios ARE this overlay's pixel constants divided
    /// by its own text height. It reads both sides rather than re-typing either, which
    /// is the only version of this test worth having: a copied number agrees with a
    /// broken implementation as happily as a correct one.</para>
    /// </summary>
    [Fact]
    public void SheetStyleKeepsTheOverlaysProportions()
    {
        double px = AnnotationGeometry.TextHeightPx;
        Assert.Equal(SheetStyle.ArrowLengthRatio, AnnotationGeometry.ArrowLengthPx / px, 12);
        Assert.Equal(SheetStyle.ArrowHalfWidthRatio, AnnotationGeometry.ArrowHalfWidthPx / px, 12);
        Assert.Equal(SheetStyle.ExtensionGapRatio, AnnotationGeometry.ExtensionGapPx / px, 12);
        Assert.Equal(SheetStyle.ExtensionOvershootRatio, AnnotationGeometry.ExtensionOvershootPx / px, 12);
        Assert.Equal(SheetStyle.TextGapRatio, AnnotationGeometry.TextGapPx / px, 12);
        Assert.Equal(SheetStyle.DefaultOffsetRatio, AnnotationGeometry.DefaultOffsetPx / px, 12);
        Assert.Equal(SheetStyle.LeaderLengthRatio, AnnotationGeometry.LeaderLengthPx / px, 12);
        Assert.Equal(SheetStyle.TailLengthRatio, AnnotationGeometry.TailLengthPx / px, 12);
        Assert.Equal(SheetStyle.BoxPaddingRatio, AnnotationGeometry.DatumBoxPaddingPx / px, 12);
        Assert.Equal(SheetStyle.LineSpacing, AnnotationGeometry.LineSpacing, 12);
        Assert.Equal(SheetStyle.ArcStepRadians, AnnotationGeometry.ArcStepRadians, 12);
    }
}
