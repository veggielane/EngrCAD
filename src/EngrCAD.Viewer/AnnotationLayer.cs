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

    // The selected annotation's own segments, drawn again in selection gold over the
    // normal batch (same geometry, so the highlight can never sit beside its lines).
    private readonly List<(Vector3d A, Vector3d B)> _selectedSegments = [];
    private bool _selectedUploaded;
    private uint _selectedVao, _selectedVbo;
    private int _selectedVertexCount;

    /// <summary>Whether any persistent (part-attached) annotations are loaded — hosts
    /// use it to decide the toolbar toggle's relevance.</summary>
    public bool HasItems => _items.Count > 0;

    /// <summary>Replaces the persistent annotation items (scene apply/live reload).
    /// Clears any selection — the indices no longer mean the same rows.</summary>
    public void SetItems(IReadOnlyList<AnnotationItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = -1;
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

    // ---- picking + selection (always-on-top overlay => depth-blind pick) ----

    private int _selected = -1;

    /// <summary>Index of the selected persistent annotation (−1 for none).</summary>
    public int SelectedIndex => _selected;

    /// <summary>The selected annotation's display text (hosts report it), or null.</summary>
    public string? SelectedText =>
        _selected >= 0 && _selected < _items.Count ? _items[_selected].Annotation.Text : null;

    /// <summary>
    /// Picks the persistent annotation nearest the ray (within
    /// <see cref="AnnotationGeometry.PickRadiusPx"/> style pixels of its drawn
    /// segments), or −1 — pure math (<see cref="AnnotationGeometry.Pick"/>), callable
    /// off the render thread. Ignored while hidden: an invisible overlay must not
    /// swallow clicks.
    /// </summary>
    public int Pick(in AnnotationCamera camera, in Ray3d ray, bool showPersistent) =>
        showPersistent ? AnnotationGeometry.Pick(_items, camera, ray) : -1;

    /// <summary>Selects (or clears, −1) an annotation; the selected one draws in
    /// selection gold. Returns true when the selection changed.</summary>
    public bool Select(int index)
    {
        int next = index >= 0 && index < _items.Count ? index : -1;
        if (next == _selected)
            return false;
        _selected = next;
        _dirty = true;
        return true;
    }

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

            // The selected item's segments again, for the gold overdraw batch.
            _selectedSegments.Clear();
            if (showPersistent && _selected >= 0 && _selected < _items.Count)
            {
                _buildScratch.Clear();
                _buildScratch.Add(_items[_selected]);
                AnnotationGeometry.Build(_selectedSegments, _buildScratch, camera);
            }
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
        if (_selectedVertexCount > 0)
        {
            // Selection gold over the normal batch — the one selection colour
            // (Highlight.Selection), never a second definition of it.
            var gold = Highlight.Selection;
            gl.Uniform3(uColor, gold.R, gold.G, gold.B);
            gl.BindVertexArray(_selectedVao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_selectedVertexCount);
        }
        gl.BindVertexArray(0);
        gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Deletes the GPU buffers (deinit path; GL context current).</summary>
    public void Release(GL gl)
    {
        if (_uploaded)
        {
            gl.DeleteBuffer(_vbo);
            gl.DeleteVertexArray(_vao);
            _uploaded = false;
            _vertexCount = 0;
        }
        if (_selectedUploaded)
        {
            gl.DeleteBuffer(_selectedVbo);
            gl.DeleteVertexArray(_selectedVao);
            _selectedUploaded = false;
            _selectedVertexCount = 0;
        }
    }

    private void Upload(GL gl)
    {
        Release(gl);
        if (_segments.Count > 0)
        {
            (_vao, _vbo) = RenderUploads.UploadLines(gl, RenderGeometry.SegmentVertices(_segments));
            _vertexCount = _segments.Count * 2;
            _uploaded = true;
        }
        if (_selectedSegments.Count > 0)
        {
            (_selectedVao, _selectedVbo) =
                RenderUploads.UploadLines(gl, RenderGeometry.SegmentVertices(_selectedSegments));
            _selectedVertexCount = _selectedSegments.Count * 2;
            _selectedUploaded = true;
        }
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
