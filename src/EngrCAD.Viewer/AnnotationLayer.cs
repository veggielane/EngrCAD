using EngrCAD.Core;
using EngrCAD.Modeling;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

// 3D annotation (PMI) rendering: dimension lines with extension lines and
// arrowheads, leader notes, datum boxes, and billboarded screen-constant text from
// the shared StrokeFont — all through the existing line program. Self-contained like
// the section-isoline and view-cube precedents: AnnotationCamera + AnnotationGeometry
// are pure math (unit-testable, no GL), AnnotationLayer owns the GL state, and
// ViewportControl/OffscreenRenderer only call Set*/Draw/Release.
//
// Deliberate choices:
// - Billboarding and screen-constant sizing are CPU-side: geometry is rebuilt only
//   when the camera pose, viewport, or annotation set changes (never allocating per
//   frame beyond the reused scratch); a rebuild is a few hundred line segments, well
//   under the cost of one part draw.
// - Depth behavior is ALWAYS-ON-TOP for v1: the annotation pass draws with the depth
//   test disabled, so dimensions read over the model from any angle (occlusion-aware
//   annotations are a follow-up). Annotations are also never section-clipped —
//   they are documentation, not model geometry.
// - Unlike the interactive view cube, annotations DO render in the headless
//   OffscreenRenderer: a docs render of a dimensioned part carries its dimensions.

/// <summary>One annotation to draw: the part-local resolved form plus the instance's
/// world transform (assembly instances pose their part's annotations).</summary>
internal readonly record struct AnnotationItem(ResolvedAnnotation Annotation, Matrix4d World);

/// <summary>
/// The camera data annotation billboarding needs, derived once per build from the
/// orbit pose: eye/basis vectors, projection kind, and the pixel-to-world conversion.
/// A record struct so value equality is the layer's rebuild key.
/// </summary>
internal readonly record struct AnnotationCamera(
    Vector3d Eye, Vector3d Forward, Vector3d Right, Vector3d Up,
    bool Orthographic, double OrthoHalfHeight, double ViewportHeightPx, double PixelScale)
{
    /// <summary>Vertical field of view of both render paths' perspective projection.</summary>
    public const double FovY = Math.PI / 4;

    /// <summary>Derives the billboarding camera from an orbit pose, mirroring
    /// CameraMath.Eye/LookAt exactly (same basis both render paths use).
    /// <paramref name="pixelScale"/> converts style pixels to framebuffer pixels
    /// (the window's render scaling, or the offscreen supersample factor).</summary>
    public static AnnotationCamera From(
        in CameraState camera, bool orthographic, double viewportHeightPx, double pixelScale)
    {
        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var forward = (camera.Target - eye).Normalized();
        var right = forward.Cross(Vector3d.UnitZ).Normalized();   // pitch is clamped shy of the poles
        var up = right.Cross(forward);
        return new AnnotationCamera(eye, forward, right, up,
            orthographic, camera.Distance * Math.Tan(Math.PI / 8), viewportHeightPx, pixelScale);
    }

    /// <summary>World length of one framebuffer pixel at a world point (perspective:
    /// scales with view depth; orthographic: constant).</summary>
    public double WorldPerPixel(in Vector3d at)
    {
        if (Orthographic)
            return 2 * OrthoHalfHeight / ViewportHeightPx;
        double depth = Math.Max((at - Eye).Dot(Forward), 1e-6);
        return 2 * depth * Math.Tan(FovY / 2) / ViewportHeightPx;
    }

    /// <summary>World length of <paramref name="px"/> style pixels at a world point.</summary>
    public double PxToWorld(double px, in Vector3d at) => px * PixelScale * WorldPerPixel(at);
}

/// <summary>
/// Pure geometry for the annotation overlay (no GL — unit-testable): builds the line
/// segments of dimension lines, extension lines, arrowheads, leaders, datum boxes,
/// and stroke-font text, billboarded to the camera and sized in screen pixels.
/// </summary>
internal static class AnnotationGeometry
{
    // Style, in logical pixels (multiplied by the camera's PixelScale).
    public const double TextHeightPx = 12;
    public const double ArrowLengthPx = 10;
    public const double ArrowHalfWidthPx = 3.5;
    public const double ExtensionGapPx = 4;        // gap between model point and extension line
    public const double ExtensionOvershootPx = 6;  // extension line past the dimension line
    public const double TextGapPx = 4;
    public const double DefaultOffsetPx = 40;      // dimension line pulled off the model
    public const double LeaderLengthPx = 36;
    public const double TailLengthPx = 14;
    public const double DatumBoxPaddingPx = 4;

    /// <summary>Builds the whole overlay into <paramref name="segments"/> (cleared
    /// first). World-space output; the layer draws it with an identity model matrix.</summary>
    public static void Build(
        List<(Vector3d A, Vector3d B)> segments, IReadOnlyList<AnnotationItem> items,
        in AnnotationCamera camera)
    {
        segments.Clear();
        foreach (var item in items)
        {
            var annotation = item.Annotation;
            var a = item.World.TransformPoint(annotation.AnchorA);
            var b = item.World.TransformPoint(annotation.AnchorB);
            var offset = item.World.TransformVector(annotation.Offset);
            switch (annotation.Kind)
            {
                case AnnotationKind.LinearDimension:
                    BuildLinear(segments, a, b, offset, annotation.Text, camera);
                    break;
                case AnnotationKind.RadialDimension:
                    BuildRadial(segments, a, b, offset, annotation.Text, camera);
                    break;
                case AnnotationKind.LeaderNote:
                    BuildLeader(segments, a, offset, annotation.Text, boxed: false, camera);
                    break;
                case AnnotationKind.DatumLabel:
                    BuildLeader(segments, a, offset, annotation.Text, boxed: true, camera);
                    break;
            }
        }
    }

    /// <summary>The classic dimension: extension lines from the anchors, a dimension
    /// line between them with arrowheads at both ends, text centered above it.</summary>
    private static void BuildLinear(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d a, in Vector3d b,
        in Vector3d offset, string text, in AnnotationCamera camera)
    {
        var mid = (a + b) * 0.5;
        var span = b - a;
        if (!span.TryNormalize(Tolerance.Default, out var measureDir))
        {
            // Degenerate (zero-length) dimension: just show the text at the point.
            AddBillboardText(segments, mid, text, camera);
            return;
        }

        // Placement: the author's offset with its along-measure component removed
        // (extension lines must be perpendicular to the dimension line); when absent
        // or degenerate, a screen-space default perpendicular to the measured span.
        var perpOffset = offset - measureDir * offset.Dot(measureDir);
        Vector3d offDir;
        double offLen;
        if (perpOffset.TryNormalize(Tolerance.Default, out var authorDir))
        {
            offDir = authorDir;
            offLen = perpOffset.Length;
        }
        else
        {
            var screenPerp = measureDir.Cross(camera.Forward);
            if (!screenPerp.TryNormalize(Tolerance.Default, out offDir))
                offDir = camera.Up;   // measuring along the view axis: any screen direction works
            offLen = camera.PxToWorld(DefaultOffsetPx, mid);
        }

        var a2 = a + offDir * offLen;
        var b2 = b + offDir * offLen;

        // Extension lines: small gap off the model, small overshoot past the line.
        segments.Add((a + offDir * camera.PxToWorld(ExtensionGapPx, a),
                      a2 + offDir * camera.PxToWorld(ExtensionOvershootPx, a2)));
        segments.Add((b + offDir * camera.PxToWorld(ExtensionGapPx, b),
                      b2 + offDir * camera.PxToWorld(ExtensionOvershootPx, b2)));

        // Dimension line + inward-pointing arrowheads at both ends.
        segments.Add((a2, b2));
        AddArrowhead(segments, a2, measureDir, offDir, camera);
        AddArrowhead(segments, b2, -measureDir, offDir, camera);

        // Text centered above the dimension line (along the offset direction).
        var textCenter = (a2 + b2) * 0.5
            + offDir * camera.PxToWorld(TextGapPx + TextHeightPx * 0.5, (a2 + b2) * 0.5);
        AddBillboardText(segments, textCenter, text, camera);
    }

    /// <summary>Radius/diameter dimension: arrow touching the circle pointing at the
    /// center, a radial leader outward, a short horizontal tail, text at its end.</summary>
    private static void BuildRadial(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d onCircle, in Vector3d center,
        in Vector3d offset, string text, in AnnotationCamera camera)
    {
        var radial = onCircle - center;
        if (!radial.TryNormalize(Tolerance.Default, out var outward))
            outward = camera.Right;

        // Arrow tip on the circle, pointing inward (wings trail outward).
        AddArrowhead(segments, onCircle, outward, PerpendicularOnScreen(outward, camera), camera);

        // Leader: outward along the radial (or the author's offset), then a tail
        // toward the horizontal text.
        var elbow = offset.LengthSquared > 0
            ? onCircle + offset
            : onCircle + outward * camera.PxToWorld(LeaderLengthPx, onCircle);
        segments.Add((onCircle, elbow));
        FinishLeaderText(segments, elbow, outward, text, boxed: false, camera);
    }

    /// <summary>Leader note / datum: arrow at the anchor, leader to the text
    /// (screen-space up-right by default), optional datum box around the text.</summary>
    private static void BuildLeader(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d anchor,
        in Vector3d offset, string text, bool boxed, in AnnotationCamera camera)
    {
        var leader = offset.LengthSquared > 0
            ? offset
            : (camera.Right + camera.Up).Normalized() * camera.PxToWorld(LeaderLengthPx, anchor);
        var elbow = anchor + leader;
        var leaderDir = leader.Normalized();

        AddArrowhead(segments, anchor, leaderDir, PerpendicularOnScreen(leaderDir, camera), camera);
        segments.Add((anchor, elbow));
        FinishLeaderText(segments, elbow, leaderDir, text, boxed, camera);
    }

    /// <summary>Shared leader ending: a short horizontal tail on the side the leader
    /// leans toward, then the text (and its datum box when <paramref name="boxed"/>).</summary>
    private static void FinishLeaderText(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d elbow, in Vector3d leaderDir,
        string text, bool boxed, in AnnotationCamera camera)
    {
        double side = leaderDir.Dot(camera.Right) < 0 ? -1 : 1;
        var tailEnd = elbow + camera.Right * (side * camera.PxToWorld(TailLengthPx, elbow));
        segments.Add((elbow, tailEnd));

        double height = camera.PxToWorld(TextHeightPx, tailEnd);
        double width = StrokeFont.TextWidth(text) * height;
        double gap = camera.PxToWorld(TextGapPx, tailEnd);
        // Baseline-left origin: text continues away from the tail, vertically centered.
        var origin = side > 0
            ? tailEnd + camera.Right * gap - camera.Up * (height * 0.5)
            : tailEnd - camera.Right * (gap + width) - camera.Up * (height * 0.5);
        StrokeFont.AppendText(segments, text, origin, camera.Right, camera.Up, height);

        if (boxed)
        {
            double pad = camera.PxToWorld(DatumBoxPaddingPx, tailEnd);
            AddBox(segments, origin, camera.Right, camera.Up, width, height, pad);
        }
    }

    /// <summary>Billboarded text centered at a world point, sized in screen pixels.</summary>
    private static void AddBillboardText(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d center, string text,
        in AnnotationCamera camera)
    {
        double height = camera.PxToWorld(TextHeightPx, center);
        double width = StrokeFont.TextWidth(text) * height;
        var origin = center - camera.Right * (width * 0.5) - camera.Up * (height * 0.5);
        StrokeFont.AppendText(segments, text, origin, camera.Right, camera.Up, height);
    }

    /// <summary>A V arrowhead: tip at <paramref name="tip"/>, wings trailing along
    /// <paramref name="direction"/> spread by <paramref name="perpendicular"/>.</summary>
    private static void AddArrowhead(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d tip, in Vector3d direction,
        in Vector3d perpendicular, in AnnotationCamera camera)
    {
        double length = camera.PxToWorld(ArrowLengthPx, tip);
        double halfWidth = camera.PxToWorld(ArrowHalfWidthPx, tip);
        var back = tip + direction * length;
        segments.Add((tip, back + perpendicular * halfWidth));
        segments.Add((tip, back - perpendicular * halfWidth));
    }

    /// <summary>A rectangle around a laid-out text run (the datum box), padded.</summary>
    private static void AddBox(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d origin,
        in Vector3d right, in Vector3d up, double width, double height, double pad)
    {
        var bl = origin - right * pad - up * pad;
        var br = origin + right * (width + pad) - up * pad;
        var tr = origin + right * (width + pad) + up * (height + pad);
        var tl = origin - right * pad + up * (height + pad);
        segments.Add((bl, br));
        segments.Add((br, tr));
        segments.Add((tr, tl));
        segments.Add((tl, bl));
    }

    /// <summary>A screen-plane direction perpendicular to <paramref name="direction"/>
    /// (arrow wings should read as wings from the current viewpoint).</summary>
    private static Vector3d PerpendicularOnScreen(in Vector3d direction, in AnnotationCamera camera)
    {
        var perpendicular = direction.Cross(camera.Forward);
        return perpendicular.TryNormalize(Tolerance.Default, out var unit) ? unit : camera.Up;
    }
}

/// <summary>
/// GL-side owner of the annotation overlay: holds the resolved items (persistent
/// per-instance annotations plus the measure tool's transient dimension), rebuilds
/// billboarded geometry only when the camera or item set changes, and draws one line
/// batch on top of the scene (depth test off — always-on-top v1; never
/// section-clipped). All GL calls require a current context (render pass).
/// </summary>
internal sealed class AnnotationLayer
{
    private static readonly (float R, float G, float B) Color = (0.92f, 0.93f, 0.97f);

    private readonly List<AnnotationItem> _items = [];
    private ResolvedAnnotation? _transient;
    private readonly List<(Vector3d A, Vector3d B)> _segments = [];
    private readonly List<AnnotationItem> _buildScratch = [];

    private bool _dirty = true;
    private AnnotationCamera _builtCamera;
    private bool _builtShowPersistent;

    private bool _uploaded;
    private uint _vao, _vbo;
    private int _vertexCount;

    /// <summary>Whether any persistent (part-attached) annotations are loaded — hosts
    /// use it to decide the toolbar toggle's relevance.</summary>
    public bool HasItems => _items.Count > 0;

    /// <summary>Replaces the persistent annotation items (scene apply/live reload).</summary>
    public void SetItems(IReadOnlyList<AnnotationItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _dirty = true;
    }

    /// <summary>Sets or clears the measure tool's transient dimension (world-space
    /// anchors, identity placement).</summary>
    public void SetTransient(ResolvedAnnotation? annotation)
    {
        _transient = annotation;
        _dirty = true;
    }

    /// <summary>True when a transient measure dimension is showing.</summary>
    public bool HasTransient => _transient is not null;

    /// <summary>
    /// Draws the overlay: rebuilds geometry when the camera pose/viewport or the item
    /// set changed (value-equality on <see cref="AnnotationCamera"/> — orbiting
    /// rebuilds, a static camera never does), then draws the batch through the shared
    /// line program with the depth test off and section clipping disabled. The
    /// caller's view/projection uniforms are already set for the frame.
    /// </summary>
    public void Draw(
        GL gl, in AnnotationCamera camera, bool showPersistent,
        uint lineProgram, int uModel, int uColor, int uSectionEnabled, Span<float> matrix)
    {
        bool anything = (showPersistent && _items.Count > 0) || _transient is not null;
        if (!anything)
            return;

        if (_dirty || camera != _builtCamera || showPersistent != _builtShowPersistent)
        {
            _buildScratch.Clear();
            if (showPersistent)
                _buildScratch.AddRange(_items);
            if (_transient is { } transient)
                _buildScratch.Add(new AnnotationItem(transient, Matrix4d.Identity));
            AnnotationGeometry.Build(_segments, _buildScratch, camera);
            Upload(gl);
            _dirty = false;
            _builtCamera = camera;
            _builtShowPersistent = showPersistent;
        }
        if (_vertexCount == 0)
            return;

        gl.UseProgram(lineProgram);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uModel, 1, false, matrix);
        gl.Uniform1(uSectionEnabled, 0f);           // documentation — never section-clipped
        gl.Uniform3(uColor, Color.R, Color.G, Color.B);

        // Always-on-top (v1): annotations must read over the model from any angle.
        gl.Disable(EnableCap.DepthTest);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_vertexCount);
        gl.BindVertexArray(0);
        gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Deletes the GPU buffers (deinit path; GL context current).</summary>
    public void Release(GL gl)
    {
        if (!_uploaded)
            return;
        gl.DeleteBuffer(_vbo);
        gl.DeleteVertexArray(_vao);
        _uploaded = false;
        _vertexCount = 0;
    }

    private void Upload(GL gl)
    {
        Release(gl);
        if (_segments.Count == 0)
            return;
        (_vao, _vbo) = RenderUploads.UploadLines(gl, RenderGeometry.SegmentVertices(_segments));
        _vertexCount = _segments.Count * 2;
        _uploaded = true;
    }

    /// <summary>
    /// One-shot headless pass for <see cref="OffscreenRenderer"/>: resolves every
    /// instance's annotations (pre-resolved by <c>Scene.PreMesh</c>; failures are
    /// skipped — a docs render should not die on one bad selector), builds the
    /// billboarded geometry for the given camera, and draws it depth-off through the
    /// line program. GL resources are not deleted — the offscreen context is
    /// destroyed wholesale after readback (the renderer's convention).
    /// </summary>
    public static void DrawOffscreen(
        GL gl, IReadOnlyList<PartInstance> instances, in AnnotationCamera camera,
        uint lineProgram, int uModel, int uColor, int uSectionEnabled, Span<float> matrix)
    {
        List<AnnotationItem>? items = null;
        foreach (var instance in instances)
        {
            if (instance.Part.Annotations.Count == 0)
                continue;
            if (!instance.Part.TryResolveAnnotations(out var resolved, out _))
                continue;
            items ??= [];
            foreach (var annotation in resolved)
                items.Add(new AnnotationItem(annotation, instance.World));
        }
        if (items is null)
            return;

        var segments = new List<(Vector3d A, Vector3d B)>();
        AnnotationGeometry.Build(segments, items, camera);
        if (segments.Count == 0)
            return;

        var (vao, _) = RenderUploads.UploadLines(gl, RenderGeometry.SegmentVertices(segments));
        gl.UseProgram(lineProgram);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uModel, 1, false, matrix);
        gl.Uniform1(uSectionEnabled, 0f);
        gl.Uniform3(uColor, Color.R, Color.G, Color.B);
        gl.Disable(EnableCap.DepthTest);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(segments.Count * 2));
        gl.BindVertexArray(0);
        gl.Enable(EnableCap.DepthTest);
    }
}
