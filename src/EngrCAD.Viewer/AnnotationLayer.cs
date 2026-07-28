using EngrCAD.Core;
using EngrCAD.Modeling;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

// The GL half of 3D annotation (PMI) rendering. The pure halves — AnnotationItem,
// AnnotationCamera and AnnotationGeometry (the dimension anatomy, billboarding and
// stroke-font text) — live in EngrCAD.Viewer.Core so the browser client shares them;
// this file keeps the GPU state and the two draw entry points.
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

/// <summary>
/// GL-side owner of the annotation overlay: holds the resolved items (persistent
/// per-instance annotations plus the measure tool's transient dimension), rebuilds
/// billboarded geometry only when the camera or item set changes, and draws one line
/// batch on top of the scene (depth test off — always-on-top v1; never
/// section-clipped). All GL calls require a current context (render pass).
/// </summary>
internal sealed class AnnotationLayer
{
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
        var color = AnnotationGeometry.Color;
        gl.Uniform3(uColor, color.R, color.G, color.B);

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
        var color = AnnotationGeometry.Color;
        gl.Uniform3(uColor, color.R, color.G, color.B);
        gl.Disable(EnableCap.DepthTest);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(segments.Count * 2));
        gl.BindVertexArray(0);
        gl.Enable(EnableCap.DepthTest);
    }
}
