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
// - Depth behavior is the caller's AnnotationDepth (default AlwaysOnTop: one batch with
//   the depth test disabled, so dimensions read over the model from any angle).
//   AnnotationDepth.Occluded draws the SAME buffer twice with two depth functions —
//   LEQUAL for the stretches nothing hides, GREATER for the rest, drawn dimmed. The
//   depth buffer already holds the scene by the time the overlay draws, so that is the
//   whole mechanism: no depth pre-pass, no CPU classification, no second geometry.
//   Annotations are never section-clipped either way — they are documentation, not
//   model geometry.
// - Unlike the interactive view cube, annotations DO render in the headless
//   OffscreenRenderer: a docs render of a dimensioned part carries its dimensions.

/// <summary>
/// GL-side owner of the annotation overlay: holds the resolved items (persistent
/// per-instance annotations plus the measure tool's transient dimension), rebuilds
/// billboarded geometry only when the camera, item set or depth mode changes, and draws
/// it over the scene at the caller's <see cref="AnnotationDepth"/> (never
/// section-clipped). All GL calls require a current context (render pass).
/// </summary>
internal sealed class AnnotationLayer
{
    private readonly List<AnnotationItem> _items = [];
    private ResolvedAnnotation? _transient;
    private readonly List<(Vector3d A, Vector3d B)> _segments = [];
    private readonly List<(Vector3d A, Vector3d B)> _textSegments = [];
    private readonly List<AnnotationItem> _buildScratch = [];
    private int _lineWorkVertexCount;

    private bool _dirty = true;
    private AnnotationCamera _builtCamera;
    private bool _builtShowPersistent;
    private AnnotationDepth _builtDepth;

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
    /// Draws the overlay: rebuilds geometry when the camera pose/viewport, the item set
    /// or the depth mode changed (value-equality on <see cref="AnnotationCamera"/> —
    /// orbiting rebuilds, a static camera never does), then draws it through the shared
    /// line program with section clipping disabled. The caller's view/projection
    /// uniforms are already set for the frame.
    /// <para><paramref name="depth"/> decides the depth behaviour; see
    /// <see cref="DrawBatches"/> for the two-depth-function mechanism.</para>
    /// </summary>
    public void Draw(
        GL gl, in AnnotationCamera camera, bool showPersistent,
        uint lineProgram, int uModel, int uColor, int uSectionEnabled, Span<float> matrix,
        AnnotationDepth depth = AnnotationDepth.AlwaysOnTop)
    {
        bool anything = (showPersistent && _items.Count > 0) || _transient is not null;
        if (!anything)
            return;

        if (_dirty || camera != _builtCamera || showPersistent != _builtShowPersistent
            || depth != _builtDepth)
        {
            // The depth bias is part of the BUILT geometry (Occluded pulls the overlay a
            // pixel toward the eye so a coplanar leader reads as on its face), which is
            // why the mode joins the rebuild key rather than being a draw-time flag.
            bool occluded = depth == AnnotationDepth.Occluded;
            double bias = occluded ? AnnotationGeometry.OccludedDepthBiasPx : 0;

            _buildScratch.Clear();
            if (showPersistent)
                _buildScratch.AddRange(_items);
            if (_transient is { } transient)
                _buildScratch.Add(new AnnotationItem(transient, Matrix4d.Identity));

            // Occluded needs the text apart from the line work, so it is built into a
            // second list and APPENDED — one upload, two ranges (the field legend's
            // trick). AlwaysOnTop passes null, which puts everything in one list in the
            // incumbent emission order, so its buffer is byte-for-byte what it was.
            _lineWorkVertexCount = 2 * AnnotationGeometry.Build(
                _segments, _buildScratch, camera, bias, occluded ? _textSegments : null);
            if (occluded)
                _segments.AddRange(_textSegments);

            // The selected item's segments again, for the gold overdraw batch — one
            // list, since the highlight is drawn always-on-top whole in either mode.
            _selectedSegments.Clear();
            if (showPersistent && _selected >= 0 && _selected < _items.Count)
            {
                _buildScratch.Clear();
                _buildScratch.Add(_items[_selected]);
                AnnotationGeometry.Build(_selectedSegments, _buildScratch, camera, bias);
            }
            Upload(gl);
            _dirty = false;
            _builtCamera = camera;
            _builtShowPersistent = showPersistent;
            _builtDepth = depth;
        }
        if (_vertexCount == 0)
            return;

        gl.UseProgram(lineProgram);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uModel, 1, false, matrix);
        gl.Uniform1(uSectionEnabled, 0f);           // documentation — never section-clipped

        DrawBatches(gl, uColor, depth, _vao, _vertexCount, _lineWorkVertexCount,
            _selectedVertexCount > 0 ? _selectedVao : 0, _selectedVertexCount);
    }

    /// <summary>
    /// The one place the depth behaviour is spelled, shared by the window and the
    /// headless pass so they cannot disagree.
    /// <para><b>AlwaysOnTop</b> is one draw with the depth test off. <b>Occluded</b> is
    /// three draws over ONE buffer: the line-work range at <c>LEQUAL</c> in
    /// <see cref="AnnotationGeometry.Color"/> (the stretches nothing hides), the SAME
    /// range again at <c>GREATER</c> in <see cref="AnnotationGeometry.HiddenColor"/>
    /// (exactly the rest), then the text range depth-off at full strength. The two depth
    /// functions partition the fragments with no overlap (LEQUAL takes equality, GREATER
    /// does not), so nothing is drawn twice and nothing is dropped — and the depth buffer
    /// already holds the scene, so no pre-pass exists.</para>
    /// <para>Depth writes are off throughout, which is what keeps the two modes
    /// comparable: a disabled depth test does not write depth either, so an overlay that
    /// wrote depth here would occlude the view cube and the legend drawn after it.</para>
    /// <para>The selection highlight stays always-on-top in BOTH modes: a selection is a
    /// UI state, not documentation about the part, and one you cannot find is not a
    /// selection.</para>
    /// </summary>
    private static void DrawBatches(
        GL gl, int uColor, AnnotationDepth depth,
        uint vao, int vertexCount, int lineWorkVertexCount,
        uint selectedVao, int selectedVertexCount)
    {
        var color = AnnotationGeometry.Color;
        gl.DepthMask(false);
        gl.BindVertexArray(vao);
        if (depth == AnnotationDepth.Occluded)
        {
            gl.Enable(EnableCap.DepthTest);
            gl.Uniform3(uColor, color.R, color.G, color.B);
            gl.DepthFunc(DepthFunction.Lequal);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)lineWorkVertexCount);

            var hidden = AnnotationGeometry.HiddenColor;
            gl.Uniform3(uColor, hidden.R, hidden.G, hidden.B);
            gl.DepthFunc(DepthFunction.Greater);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)lineWorkVertexCount);
            gl.DepthFunc(DepthFunction.Less);   // the GL default every other pass assumes

            // The value, at full strength whichever side of the part it sits on.
            gl.Disable(EnableCap.DepthTest);
            gl.Uniform3(uColor, color.R, color.G, color.B);
            gl.DrawArrays(PrimitiveType.Lines, lineWorkVertexCount,
                (uint)(vertexCount - lineWorkVertexCount));
        }
        else
        {
            gl.Disable(EnableCap.DepthTest);
            gl.Uniform3(uColor, color.R, color.G, color.B);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)vertexCount);
        }

        if (selectedVertexCount > 0)
        {
            // Selection gold over the normal batch — the one selection colour
            // (Highlight.Selection), never a second definition of it.
            var gold = Highlight.Selection;
            gl.Disable(EnableCap.DepthTest);
            gl.Uniform3(uColor, gold.R, gold.G, gold.B);
            gl.BindVertexArray(selectedVao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)selectedVertexCount);
        }
        gl.BindVertexArray(0);
        gl.DepthMask(true);
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
    /// billboarded geometry for the given camera, and draws it through the line program
    /// at the requested <paramref name="depth"/> — the SAME <see cref="DrawBatches"/>
    /// rule the window uses, so a docs render and the viewport cannot disagree about
    /// what a hidden dimension looks like. GL resources are not deleted — the offscreen
    /// context is destroyed wholesale after readback (the renderer's convention).
    /// </summary>
    public static void DrawOffscreen(
        GL gl, IReadOnlyList<PartInstance> instances, in AnnotationCamera camera,
        uint lineProgram, int uModel, int uColor, int uSectionEnabled, Span<float> matrix,
        AnnotationDepth depth = AnnotationDepth.AlwaysOnTop)
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

        bool occluded = depth == AnnotationDepth.Occluded;
        var segments = new List<(Vector3d A, Vector3d B)>();
        var text = occluded ? new List<(Vector3d A, Vector3d B)>() : null;
        int lineWork = AnnotationGeometry.Build(segments, items, camera,
            occluded ? AnnotationGeometry.OccludedDepthBiasPx : 0, text);
        if (text is not null)
            segments.AddRange(text);
        if (segments.Count == 0)
            return;

        var (vao, _) = RenderUploads.UploadLines(gl, RenderGeometry.SegmentVertices(segments));
        gl.UseProgram(lineProgram);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uModel, 1, false, matrix);
        gl.Uniform1(uSectionEnabled, 0f);
        DrawBatches(gl, uColor, depth, vao, segments.Count * 2, lineWork * 2,
            selectedVao: 0, selectedVertexCount: 0);
    }
}
