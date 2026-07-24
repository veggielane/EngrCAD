using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Silk.NET.Core.Contexts;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

/// <summary>Orbit camera pose, snapshotable for persistence across process restarts.</summary>
public sealed record CameraState(double Yaw, double Pitch, double Distance, Vector3d Target);

/// <summary>
/// OpenGL viewport rendering kernel meshes with an orbit camera.
/// Left-drag orbits, right/middle-drag pans, wheel zooms. Z is up.
/// Works on desktop GL 3.3+ and (via ANGLE on Windows) OpenGL ES 3.
/// </summary>
public sealed class ViewportControl : OpenGlControlBase
{
    private GL? _gl;
    private uint _program;
    private int _uModel, _uView, _uProj, _uColor, _uLightDir, _uEyePos, _uHighlight;
    private int _uSectionEnabled, _uSectionZ, _uAlpha;
    private uint _lineProgram;
    private int _uLineModel, _uLineView, _uLineProj, _uLineColor;
    private int _uLineSectionEnabled, _uLineSectionZ;
    private uint _bgProgram, _bgVao;
    private uint _gridVao, _gridVbo;
    private int _gridCount;
    private uint _axesVao, _axesVbo;
    private readonly List<GpuMesh> _meshes = [];
    private readonly List<bool> _visible = [];
    private readonly List<PartInstance> _instances = [];

    // GPU buffers are shared between instances of the same Part (uploaded once per
    // distinct part); this list owns them for deletion — never delete via _meshes,
    // which references the same ids once per instance.
    private readonly List<(uint Vao, uint Vbo, uint Ebo, uint EdgeVao, uint EdgeVbo, uint WireVao, uint WireVbo)>
        _gpuBuffers = [];

    private readonly object _sceneLock = new();
    private (IReadOnlyList<PartInstance> Instances, bool Frame)? _pending;

    private double _yaw = 0.7;
    private double _pitch = 0.45;
    private double _distance = 15.0;
    private Vector3d _target = (0, 1.6, 0.2);
    private Aabb _sceneBounds = Aabb.Empty;   // all parts' world bounds, for frustum/zoom scaling
    private Point _lastPointer;
    private Point _pressPointer;
    private int _selected = -1;

    private readonly record struct GpuMesh(
        uint Vao, uint Vbo, uint Ebo, int IndexCount, Matrix4d Model, (float R, float G, float B) Color,
        uint EdgeVao, uint EdgeVbo, int EdgeVertexCount,
        uint WireVao, uint WireVbo, int WireVertexCount, Vector3d WorldCenter);

    /// <summary>CPU-side pick data per object: triangles plus a BVH over them, in object space.</summary>
    private sealed record PickData(RenderMesh Mesh, EngrCAD.Core.Spatial.Bvh Bvh);

    private readonly List<PickData> _pickData = [];

    // Reusable scratch for the translucent back-to-front sort (no per-frame allocation).
    private int[] _translucentOrder = [];
    private double[] _translucentDepth = [];

    /// <summary>
    /// Replaces the displayed parts (one tab's worth of loose parts, each posed by its
    /// own <see cref="Part.Transform"/>). Convenience wrapper over
    /// <see cref="SetInstances"/> for hosts without assemblies.
    /// </summary>
    public void SetParts(IReadOnlyList<Part> parts, bool frame) =>
        SetInstances([.. parts.Select(p => new PartInstance(p, p.Transform, p.Name))], frame);

    /// <summary>
    /// Replaces the displayed instances (one tab's worth — <c>Tab.Instances()</c>).
    /// Thread-safe: geometry is uploaded on the next rendered frame (the GL context is
    /// only current there); instances sharing a <see cref="Part"/> share its GPU
    /// buffers and draw with their own world matrices. With <paramref name="frame"/>
    /// the camera auto-frames to the instances' bounds, otherwise it is left
    /// untouched. Parts should be pre-meshed (<c>Scene.PreMesh</c>) so no tessellation
    /// happens on the render thread.
    /// </summary>
    public void SetInstances(IReadOnlyList<PartInstance> instances, bool frame)
    {
        lock (_sceneLock)
            _pending = (instances, frame);
        Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    /// <summary>Shows a message in the status overlay (used by script hosts for errors).</summary>
    public void ShowStatus(string message) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Report(message));

    /// <summary>
    /// The current camera pose. Setting it suppresses first-scene auto-framing — used
    /// by <see cref="EngrCad.ShowLive"/> to restore the view across process restarts.
    /// </summary>
    public CameraState Camera
    {
        get => new(_yaw, _pitch, _distance, _target);
        set
        {
            _yaw = value.Yaw;
            _pitch = Math.Clamp(value.Pitch, -Math.PI / 2 + 0.01, Math.PI / 2 - 0.01);
            _distance = Math.Clamp(value.Distance, 0.5, CameraMath.MaxOrbitDistance(_sceneBounds));
            _target = value.Target;
            lock (_sceneLock)
            {
                // An explicit pose wins over any auto-framing still queued.
                if (_pending is { } pending)
                    _pending = (pending.Instances, false);
            }
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(new LamdaNativeContext(name => gl.GetProcAddress(name)));
        _program = CompileProgram(_gl);
        _uModel = _gl.GetUniformLocation(_program, "uModel");
        _uView = _gl.GetUniformLocation(_program, "uView");
        _uProj = _gl.GetUniformLocation(_program, "uProj");
        _uColor = _gl.GetUniformLocation(_program, "uColor");
        _uLightDir = _gl.GetUniformLocation(_program, "uLightDir");
        _uEyePos = _gl.GetUniformLocation(_program, "uEyePos");
        _uHighlight = _gl.GetUniformLocation(_program, "uHighlight");
        _uSectionEnabled = _gl.GetUniformLocation(_program, "uSectionEnabled");
        _uSectionZ = _gl.GetUniformLocation(_program, "uSectionZ");
        _uAlpha = _gl.GetUniformLocation(_program, "uAlpha");

        _lineProgram = CompileLineProgram(_gl);
        _uLineModel = _gl.GetUniformLocation(_lineProgram, "uModel");
        _uLineView = _gl.GetUniformLocation(_lineProgram, "uView");
        _uLineProj = _gl.GetUniformLocation(_lineProgram, "uProj");
        _uLineColor = _gl.GetUniformLocation(_lineProgram, "uColor");
        _uLineSectionEnabled = _gl.GetUniformLocation(_lineProgram, "uSectionEnabled");
        _uLineSectionZ = _gl.GetUniformLocation(_lineProgram, "uSectionZ");

        _bgProgram = CompileBackgroundProgram(_gl);
        _bgVao = _gl.GenVertexArray(); // vertexless fullscreen triangle via gl_VertexID
    }

    /// <summary>One distinct part's GPU buffers + pick data, shared by its instances.</summary>
    private readonly record struct SharedMesh(
        uint Vao, uint Vbo, uint Ebo, int IndexCount,
        uint EdgeVao, uint EdgeVbo, int EdgeVertexCount,
        uint WireVao, uint WireVbo, int WireVertexCount, PickData Pick);

    /// <summary>Uploads an instance list, replacing existing GPU resources. Each
    /// distinct part is prepared and uploaded once (cached display mesh → RenderMesh,
    /// feature/wire edges, pick BVH); instances reference the shared buffers with
    /// per-instance model matrices. GL context must be current.</summary>
    private void ApplyInstances(GL gl, IReadOnlyList<PartInstance> instances, bool frame)
    {
        DeleteMeshBuffers(gl);
        lock (_sceneLock)
        {
            _meshes.Clear();
            _pickData.Clear();
            _visible.Clear();
            _instances.Clear();
            _selected = -1;

            var shared = new Dictionary<Part, SharedMesh>(); // Part has reference identity
            var bounds = Aabb.Empty;
            foreach (var instance in instances)
            {
                var part = instance.Part;
                if (!shared.TryGetValue(part, out var s))
                {
                    s = UploadShared(gl, part);
                    shared[part] = s;
                }

                var color = part.Color ?? Palette.Steel;
                var worldBounds = instance.Bounds();
                _meshes.Add(new GpuMesh(
                    s.Vao, s.Vbo, s.Ebo, s.IndexCount, instance.World, (color.R, color.G, color.B),
                    s.EdgeVao, s.EdgeVbo, s.EdgeVertexCount,
                    s.WireVao, s.WireVbo, s.WireVertexCount,
                    worldBounds.IsEmpty ? Vector3d.Zero : worldBounds.Center));
                _pickData.Add(s.Pick);
                _visible.Add(true);
                _instances.Add(instance);
                bounds = bounds.Union(worldBounds);
            }

            RebuildGrid(gl, bounds);
            _sceneBounds = bounds;

            if (frame && !bounds.IsEmpty)
            {
                _target = bounds.Center;
                _distance = CameraMath.FrameDistance(bounds);
            }
        }
    }

    /// <summary>Uploads one distinct part's buffers and registers them for deletion.</summary>
    private SharedMesh UploadShared(GL gl, Part part)
    {
        var mesh = part.GetMesh();
        var render = RenderMesh.CreateFlat(mesh);
        var featureEdges = MeshFeatureEdges.Extract(mesh);
        var wireEdges = WireframeEdges.Extract(mesh);
        var (vao, vbo, ebo) = RenderGeometry.UploadMesh(gl, render);
        var (edgeVao, edgeVbo) = RenderGeometry.UploadLines(gl, RenderGeometry.SegmentVertices(featureEdges));
        var (wireVao, wireVbo) = RenderGeometry.UploadLines(gl, RenderGeometry.SegmentVertices(wireEdges));
        _gpuBuffers.Add((vao, vbo, ebo, edgeVao, edgeVbo, wireVao, wireVbo));

        var boxes = new Aabb[render.TriangleCount];
        for (int t = 0; t < render.TriangleCount; t++)
        {
            boxes[t] = Aabb.FromPoints(
            [
                PickVertex(render, render.Indices[t * 3]),
                PickVertex(render, render.Indices[t * 3 + 1]),
                PickVertex(render, render.Indices[t * 3 + 2]),
            ]);
        }
        return new SharedMesh(vao, vbo, ebo, render.Indices.Length,
            edgeVao, edgeVbo, featureEdges.Count * 2,
            wireVao, wireVbo, wireEdges.Count * 2,
            new PickData(render, EngrCAD.Core.Spatial.Bvh.Build(boxes)));
    }

    /// <summary>Deletes the per-part GPU buffers (once each — instances share them).</summary>
    private void DeleteMeshBuffers(GL gl)
    {
        foreach (var b in _gpuBuffers)
        {
            gl.DeleteBuffer(b.Vbo);
            gl.DeleteBuffer(b.Ebo);
            gl.DeleteVertexArray(b.Vao);
            gl.DeleteBuffer(b.EdgeVbo);
            gl.DeleteVertexArray(b.EdgeVao);
            gl.DeleteBuffer(b.WireVbo);
            gl.DeleteVertexArray(b.WireVao);
        }
        _gpuBuffers.Clear();
    }

    /// <summary>Ground grid on z = 0 sized to the scene, plus RGB world axes.</summary>
    private void RebuildGrid(GL gl, in Aabb bounds)
    {
        if (_gridVbo != 0)
        {
            gl.DeleteBuffer(_gridVbo);
            gl.DeleteVertexArray(_gridVao);
            gl.DeleteBuffer(_axesVbo);
            gl.DeleteVertexArray(_axesVao);
        }

        var (gridVertices, axesVertices) = RenderGeometry.BuildGridAndAxes(bounds);
        _gridCount = gridVertices.Length / 3;
        (_gridVao, _gridVbo) = RenderGeometry.UploadLines(gl, gridVertices);
        (_axesVao, _axesVbo) = RenderGeometry.UploadLines(gl, axesVertices);
    }

    private static Vector3d PickVertex(RenderMesh mesh, uint index) => new(
        mesh.Positions[index * 3],
        mesh.Positions[index * 3 + 1],
        mesh.Positions[index * 3 + 2]);

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is null)
            return;
        DeleteMeshBuffers(_gl);
        _meshes.Clear();
        if (_gridVbo != 0)
        {
            _gl.DeleteBuffer(_gridVbo);
            _gl.DeleteVertexArray(_gridVao);
            _gl.DeleteBuffer(_axesVbo);
            _gl.DeleteVertexArray(_axesVao);
            _gridVbo = 0;
        }
        _gl.DeleteVertexArray(_bgVao);
        _gl.DeleteProgram(_program);
        _gl.DeleteProgram(_lineProgram);
        _gl.DeleteProgram(_bgProgram);
        _gl.Dispose();
        _gl = null;
    }

    protected override unsafe void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl is null)
            return;
        var gl = _gl;

        (IReadOnlyList<PartInstance> Instances, bool Frame)? update = null;
        lock (_sceneLock)
        {
            if (_pending is not null)
            {
                update = _pending;
                _pending = null;
            }
        }
        if (update is { } u)
            ApplyInstances(gl, u.Instances, u.Frame);

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        uint width = (uint)Math.Max(1, Bounds.Width * scaling);
        uint height = (uint)Math.Max(1, Bounds.Height * scaling);
        gl.Viewport(0, 0, width, height);

        gl.ClearColor(0.11f, 0.12f, 0.14f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // Background gradient: vertexless fullscreen triangle, no depth.
        gl.Disable(EnableCap.DepthTest);
        gl.UseProgram(_bgProgram);
        gl.BindVertexArray(_bgVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.Enable(EnableCap.DepthTest);

        var eye = CameraMath.Eye(_yaw, _pitch, _distance, _target);
        var view = CameraMath.LookAt(eye, _target, Vector3d.UnitZ);
        var proj = ProjectionMatrix((double)width / height);

        Span<float> matrix = stackalloc float[16];

        // Ground grid + world axes.
        gl.UseProgram(_lineProgram);
        CameraMath.WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(_uLineView, 1, false, matrix);
        CameraMath.WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(_uLineProj, 1, false, matrix);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(_uLineModel, 1, false, matrix);
        gl.Uniform1(_uLineSectionEnabled, 0f);   // grid/axes are scene furniture — never clipped
        if (_gridVbo != 0)
        {
            gl.Uniform3(_uLineColor, 0.24f, 0.26f, 0.29f);
            gl.BindVertexArray(_gridVao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_gridCount);

            gl.BindVertexArray(_axesVao);
            gl.Uniform3(_uLineColor, 0.75f, 0.30f, 0.30f);
            gl.DrawArrays(PrimitiveType.Lines, 0, 2);           // +X
            gl.Uniform3(_uLineColor, 0.33f, 0.66f, 0.33f);
            gl.DrawArrays(PrimitiveType.Lines, 2, 2);           // +Y
            gl.Uniform3(_uLineColor, 0.35f, 0.48f, 0.85f);
            gl.DrawArrays(PrimitiveType.Lines, 4, 2);           // +Z
        }

        // Shaded fills, pushed back slightly so the edge overlay wins the depth test.
        gl.UseProgram(_program);
        CameraMath.WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(_uView, 1, false, matrix);
        CameraMath.WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(_uProj, 1, false, matrix);
        var lightDir = new Vector3d(-0.5, -0.7, -0.9).Normalized();
        gl.Uniform3(_uLightDir, (float)lightDir.X, (float)lightDir.Y, (float)lightDir.Z);
        gl.Uniform3(_uEyePos, (float)eye.X, (float)eye.Y, (float)eye.Z);
        gl.Uniform1(_uSectionEnabled, _sectionEnabled ? 1f : 0f);
        gl.Uniform1(_uSectionZ, (float)_sectionHeight);

        // Section mode relies on face culling staying OFF (the GL default; nothing here
        // enables CullFace): clipping a closed solid exposes its interior, and the
        // fragment shader shades those backfaces as cut material via gl_FrontFacing.
        // Fills push back slightly (polygon offset) so the edge overlay wins depth.
        gl.Uniform1(_uAlpha, 1f);
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1f, 1f);
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i] || Mode(i) != DisplayMode.Shaded)
                continue;
            DrawFill(gl, i, matrix);
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        // Line overlay: feature edges for shaded parts (the shaded-with-edges CAD
        // look), full triangle wireframe for wireframe-mode parts. Lines belong to
        // the model, so the section plane clips them consistently with the fills.
        gl.UseProgram(_lineProgram);
        gl.Uniform1(_uLineSectionEnabled, _sectionEnabled ? 1f : 0f);
        gl.Uniform1(_uLineSectionZ, (float)_sectionHeight);
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i])
                continue;
            var m = _meshes[i];
            switch (Mode(i))
            {
                case DisplayMode.Wireframe when m.WireVertexCount > 0:
                    CameraMath.WriteColumnMajor(m.Model, matrix);
                    gl.UniformMatrix4(_uLineModel, 1, false, matrix);
                    if (i == _selected)
                        gl.Uniform3(_uLineColor, 1.0f, 0.85f, 0.35f);
                    else
                        gl.Uniform3(_uLineColor, m.Color.R, m.Color.G, m.Color.B);
                    gl.BindVertexArray(m.WireVao);
                    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)m.WireVertexCount);
                    break;
                case DisplayMode.Shaded when m.EdgeVertexCount > 0:
                    DrawFeatureEdges(gl, i, matrix);
                    break;
            }
        }

        // Translucent pass, after everything opaque: blended fills back-to-front by
        // part center, with depth writes off so translucent parts do not occlude each
        // other; depth *testing* stays on so opaque geometry in front still wins.
        // Order/depth scratch buffers are reused frame to frame (render paths must not
        // allocate per frame); the sort is an insertion sort over the depth keys, so no
        // comparer delegate is created either.
        if (_translucentOrder.Length < _meshes.Count)
        {
            _translucentOrder = new int[_meshes.Count];
            _translucentDepth = new double[_meshes.Count];
        }
        int translucentCount = 0;
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (_visible[i] && Mode(i) == DisplayMode.Translucent)
            {
                _translucentOrder[translucentCount] = i;
                _translucentDepth[translucentCount] = (_meshes[i].WorldCenter - eye).LengthSquared;
                translucentCount++;
            }
        }
        if (translucentCount > 0)
        {
            // Farthest first (descending depth): back-to-front blending.
            for (int k = 1; k < translucentCount; k++)
            {
                int index = _translucentOrder[k];
                double depth = _translucentDepth[k];
                int j = k - 1;
                while (j >= 0 && _translucentDepth[j] < depth)
                {
                    _translucentOrder[j + 1] = _translucentOrder[j];
                    _translucentDepth[j + 1] = _translucentDepth[j];
                    j--;
                }
                _translucentOrder[j + 1] = index;
                _translucentDepth[j + 1] = depth;
            }

            gl.UseProgram(_program);
            gl.Uniform1(_uAlpha, 0.4f);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(false);
            for (int k = 0; k < translucentCount; k++)
                DrawFill(gl, _translucentOrder[k], matrix);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);

            // Their feature edges draw opaque on top (the fills wrote no depth), which
            // keeps the part's silhouette readable through the glass.
            gl.UseProgram(_lineProgram);
            for (int k = 0; k < translucentCount; k++)
            {
                if (_meshes[_translucentOrder[k]].EdgeVertexCount > 0)
                    DrawFeatureEdges(gl, _translucentOrder[k], matrix);
            }
        }
        gl.BindVertexArray(0);

        // A requested screenshot reads back the finished frame while the context is
        // current (GL calls are only legal here), then encodes off-thread.
        if (Interlocked.Exchange(ref _pendingScreenshot, null) is { } screenshotPath)
            CaptureFramebuffer(gl, (int)width, (int)height, screenshotPath);
    }

    private DisplayMode Mode(int index) => _instances[index].Part.DisplayMode;

    private unsafe void DrawFill(GL gl, int index, Span<float> matrix)
    {
        var m = _meshes[index];
        CameraMath.WriteColumnMajor(m.Model, matrix);
        gl.UniformMatrix4(_uModel, 1, false, matrix);
        gl.Uniform3(_uColor, m.Color.R, m.Color.G, m.Color.B);
        gl.Uniform1(_uHighlight, index == _selected ? 1f : 0f);
        gl.BindVertexArray(m.Vao);
        gl.DrawElements(PrimitiveType.Triangles, (uint)m.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    private void DrawFeatureEdges(GL gl, int index, Span<float> matrix)
    {
        var m = _meshes[index];
        CameraMath.WriteColumnMajor(m.Model, matrix);
        gl.UniformMatrix4(_uLineModel, 1, false, matrix);
        if (index == _selected)
            gl.Uniform3(_uLineColor, 1.0f, 0.85f, 0.35f);
        else
            gl.Uniform3(_uLineColor, 0.09f, 0.10f, 0.11f);
        gl.BindVertexArray(m.EdgeVao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)m.EdgeVertexCount);
    }

    // ---- picking ----

    /// <summary>Optional status sink for the overlay: shows the last input seen (debugging aid).</summary>
    public Action<string>? Status { get; set; }

    private void Report(string message) => Status?.Invoke(message);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Input is registered at the window level with handledEventsToo so that nothing
        // upstream (gesture recognizers, hit-test quirks over the GL surface) can starve
        // the viewport of events — trackpads proved fragile with control-level handlers.
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        top.AddHandler(PointerPressedEvent, (_, args) => HandlePressed(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerMovedEvent, (_, args) => HandleMoved(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerReleasedEvent, (_, args) => HandleReleased(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerWheelChangedEvent, (_, args) => HandleWheel(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(KeyDownEvent, (_, args) => HandleKey(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void Pick(Point pixel)
    {
        double width = Math.Max(1, Bounds.Width);
        double height = Math.Max(1, Bounds.Height);
        var eye = CameraMath.Eye(_yaw, _pitch, _distance, _target);
        var viewProjection = ProjectionMatrix(width / height)
                           * CameraMath.LookAt(eye, _target, Vector3d.UnitZ);
        if (!viewProjection.TryInvert(out var unproject))
            return;

        double ndcX = 2 * pixel.X / width - 1;
        double ndcY = 1 - 2 * pixel.Y / height;
        var nearPoint = unproject.TransformPoint((ndcX, ndcY, -1));
        var farPoint = unproject.TransformPoint((ndcX, ndcY, 1));

        int best = -1;
        double bestT = double.PositiveInfinity;
        var hits = new List<int>();
        for (int i = 0; i < _pickData.Count; i++)
        {
            if (!_visible[i])
                continue;
            if (!_meshes[i].Model.TryInvert(out var toLocal))
                continue;
            var origin = toLocal.TransformPoint(nearPoint);
            var direction = toLocal.TransformPoint(farPoint) - origin;
            var ray = new Ray3d(origin, direction);

            hits.Clear();
            _pickData[i].Bvh.Query(ray, hits);
            foreach (int triangle in hits)
            {
                var mesh = _pickData[i].Mesh;
                var a = PickVertex(mesh, mesh.Indices[triangle * 3]);
                var b = PickVertex(mesh, mesh.Indices[triangle * 3 + 1]);
                var c = PickVertex(mesh, mesh.Indices[triangle * 3 + 2]);
                if (RayTriangle(ray, a, b, c, out double t) && t < bestT)
                {
                    bestT = t;
                    best = i;
                }
            }
        }
        Select(best == _selected ? -1 : best); // clicking the selection clears it
        Report(_selected >= 0 ? $"picked '{_instances[_selected].Path}'" : "picked nothing");
        SelectionChanged?.Invoke(_selected);
    }

    /// <summary>Raised when a click changes the selection (−1 = nothing); UI thread.</summary>
    public event Action<int>? SelectionChanged;

    /// <summary>Index of the selected part, −1 for none.</summary>
    public int Selected => _selected;

    /// <summary>Sets the selection programmatically (tree clicks); does not raise
    /// <see cref="SelectionChanged"/>.</summary>
    public void Select(int index)
    {
        _selected = index >= 0 && index < _instances.Count ? index : -1;
        string? name = _selected >= 0 ? _instances[_selected].Path : null;
        if (VisualRoot is Window window)
            window.Title = name is not null ? $"{BaseTitle} — {name}" : BaseTitle;
        RequestNextFrameRendering();
    }

    /// <summary>Shows or hides a part (index into the current part list).</summary>
    public void SetVisible(int index, bool visible)
    {
        lock (_sceneLock)
        {
            if (index >= 0 && index < _visible.Count)
                _visible[index] = visible;
        }
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Changes how a part is drawn (index into the current part list). Writes through
    /// to <see cref="Part.DisplayMode"/>, so the mode survives tab switches; a live
    /// reload rebuilds parts and the model code's modes win again.
    /// </summary>
    public void SetDisplayMode(int index, DisplayMode mode)
    {
        lock (_sceneLock)
        {
            if (index >= 0 && index < _instances.Count)
                _instances[index].Part.DisplayMode = mode;
        }
        RequestNextFrameRendering();
    }

    /// <summary>The display mode of a part (index into the current part list).</summary>
    public DisplayMode GetDisplayMode(int index)
    {
        lock (_sceneLock)
            return index >= 0 && index < _instances.Count
                ? _instances[index].Part.DisplayMode
                : DisplayMode.Shaded;
    }

    // ---- screenshot ----

    private string? _pendingScreenshot;

    /// <summary>
    /// Saves the next rendered frame as a PNG. Thread-safe: the pixels are read inside
    /// the render pass (the only place GL calls are legal) and encoded off-thread; the
    /// resulting path (or failure) is reported through <see cref="Status"/>.
    /// With no <paramref name="path"/>, writes a timestamped file under
    /// Pictures/EngrCAD (falling back to the working directory).
    /// </summary>
    public void SaveScreenshot(string? path = null)
    {
        Interlocked.Exchange(ref _pendingScreenshot, path ?? DefaultScreenshotPath());
        Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    private static string DefaultScreenshotPath()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        folder = string.IsNullOrEmpty(folder)
            ? Environment.CurrentDirectory
            : Path.Combine(folder, "EngrCAD");
        return Path.Combine(folder, $"engrcad-{DateTime.Now:yyyyMMdd-HHmmss}.png");
    }

    /// <summary>Reads the finished frame (GL context current) and encodes/writes it off-thread.</summary>
    private unsafe void CaptureFramebuffer(GL gl, int width, int height, string path)
    {
        var pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        Task.Run(() =>
        {
            try
            {
                // GL rows are bottom-up; the framebuffer alpha is compositing residue.
                var png = PngWriter.Encode(width, height, pixels, flipVertically: true, forceOpaque: true);
                if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(path, png);
                ShowStatus($"screenshot saved: {path}");
            }
            catch (Exception e)
            {
                ShowStatus($"screenshot failed: {e.Message}");
            }
        });
    }

    /// <summary>Zoom-to-fit: frames the camera on the visible parts' bounds.</summary>
    public void Frame()
    {
        var bounds = Aabb.Empty;
        lock (_sceneLock)
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                if (_visible[i])
                    bounds = bounds.Union(_instances[i].Bounds());
            }
        }
        if (!bounds.IsEmpty)
        {
            _target = bounds.Center;
            _distance = CameraMath.FrameDistance(bounds);
        }
        RequestNextFrameRendering();
    }

    /// <summary>Window title stem, restored when nothing is selected.</summary>
    public string BaseTitle { get; set; } = "EngrCAD";

    private bool _orthographic;

    /// <summary>Orthographic (true) vs perspective (false, default) projection. The
    /// orthographic frustum is sized so the target plane keeps its apparent size.</summary>
    public bool Orthographic
    {
        get => _orthographic;
        set
        {
            _orthographic = value;
            RequestNextFrameRendering();
        }
    }

    // ---- section plane ----

    private bool _sectionEnabled;
    private double _sectionHeight;
    private bool _sectionHeightSet;

    /// <summary>
    /// Section mode: clips the model at a horizontal plane (world z = <see cref="SectionHeight"/>)
    /// to reveal interiors — exposed backfaces render as flat cut material. When first
    /// enabled, the height defaults to the middle of the current parts' bounds.
    /// Picking ignores the section plane (v1).
    /// </summary>
    public bool SectionEnabled
    {
        get => _sectionEnabled;
        set
        {
            _sectionEnabled = value;
            if (value && !_sectionHeightSet)
            {
                var bounds = PartsBounds();
                if (!bounds.IsEmpty)
                {
                    _sectionHeight = bounds.Center.Z;
                    _sectionHeightSet = true;
                }
            }
            RequestNextFrameRendering();
        }
    }

    /// <summary>World-z height of the section plane; geometry above it is clipped.</summary>
    public double SectionHeight
    {
        get => _sectionHeight;
        set
        {
            _sectionHeight = value;
            _sectionHeightSet = true;
            RequestNextFrameRendering();
        }
    }

    /// <summary>Moves the section plane by 2% of the parts' bounds height per step.</summary>
    private void NudgeSection(int direction)
    {
        var bounds = PartsBounds();
        double extent = bounds.IsEmpty ? 10 : bounds.Size.Z;
        SectionHeight += direction * extent * 0.02;
        Report($"section z = {_sectionHeight:G4}");
    }

    /// <summary>Union of all parts' world bounds (empty when no scene is loaded).</summary>
    private Aabb PartsBounds()
    {
        var bounds = Aabb.Empty;
        lock (_sceneLock)
        {
            foreach (var instance in _instances)
                bounds = bounds.Union(instance.Bounds());
        }
        return bounds;
    }

    private Matrix4d ProjectionMatrix(double aspect)
    {
        // Near/far scale with the camera and scene (shared with the offscreen pass),
        // so large scenes neither clip at a fixed far plane nor need a distance clamp.
        var (near, far) = CameraMath.FrustumPlanes(_distance, _sceneBounds);
        return _orthographic
            ? CameraMath.Orthographic(_distance * Math.Tan(Math.PI / 8), aspect, near, far)
            : CameraMath.Perspective(Math.PI / 4, aspect, near, far);
    }

    /// <summary>Möller–Trumbore; t is in units of the (unnormalized) ray direction.</summary>
    private static bool RayTriangle(in Ray3d ray, in Vector3d a, in Vector3d b, in Vector3d c, out double t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = ray.Direction.Cross(e2);
        double determinant = e1.Dot(p);
        // Round-off-scale parallel-ray guard (picking robustness, not model geometry:
        // a missed edge-on triangle costs a click, never a weld).
        if (Math.Abs(determinant) < 1e-15)
            return false;
        double inverse = 1.0 / determinant;
        var s = ray.Origin - a;
        double u = s.Dot(p) * inverse;
        if (u < 0 || u > 1)
            return false;
        var q = s.Cross(e1);
        double v = ray.Direction.Dot(q) * inverse;
        if (v < 0 || u + v > 1)
            return false;
        t = e2.Dot(q) * inverse;
        // Minimum hit distance (direction-length units): rejects self-hits at the ray
        // origin; a UI-picking threshold, not a kernel tolerance.
        return t > 1e-9;
    }

    // Shader sources live in ViewerShaders (RenderCore.cs), shared verbatim with the
    // headless OffscreenRenderer so the two passes cannot drift; only the version
    // header differs (desktop GL 3.3 vs GLES3 via ANGLE, chosen at runtime here).

    private string ShaderHeader => ViewerShaders.Header(GlVersion.Type == GlProfileType.OpenGLES);

    private uint CompileProgram(GL gl) => ViewerShaders.LinkProgram(
        gl, ShaderHeader + ViewerShaders.MeshVertex, ShaderHeader + ViewerShaders.MeshFragment,
        bindAttributes: true);

    private uint CompileLineProgram(GL gl) => ViewerShaders.LinkProgram(
        gl, ShaderHeader + ViewerShaders.LineVertex, ShaderHeader + ViewerShaders.LineFragment,
        bindAttributes: true);

    private uint CompileBackgroundProgram(GL gl) => ViewerShaders.LinkProgram(
        gl, ShaderHeader + ViewerShaders.BackgroundVertex, ShaderHeader + ViewerShaders.BackgroundFragment,
        bindAttributes: false);

    // ---- camera ----

    private void HandlePressed(PointerPressedEventArgs e)
    {
        _lastPointer = e.GetPosition(this);
        _pressPointer = _lastPointer;
        Report($"press at {_pressPointer.X:F0},{_pressPointer.Y:F0}");
    }

    private void HandleMoved(PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var pos = point.Position;
        var delta = pos - _lastPointer;
        _lastPointer = pos;

        // Laptop-friendly: everything reachable from the primary button + modifiers.
        // Shift+drag (or right/middle drag) pans, Ctrl+drag zooms, plain drag orbits.
        bool pan = point.Properties.IsRightButtonPressed
                   || point.Properties.IsMiddleButtonPressed
                   || (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        bool zoom = point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (pan)
        {
            Pan(delta.X, delta.Y);
            Report("pan (drag)");
        }
        else if (zoom)
        {
            Zoom(Math.Pow(1.006, delta.Y));
            Report("zoom (drag)");
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            Orbit(-delta.X * 0.01, delta.Y * 0.01);
            Report("orbit (drag)");
        }
    }

    private void HandleReleased(PointerReleasedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var moved = pos - _pressPointer;
        if (moved.X * moved.X + moved.Y * moved.Y < 16)
            Pick(pos);
        else
            Report("release (drag end)");
    }

    private void HandleWheel(PointerWheelEventArgs e)
    {
        // Trackpad two-finger scrolls arrive as many small fractional deltas.
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        Zoom(Math.Pow(0.88, delta));
        Report($"wheel Δ{delta:F2}");
    }

    private void HandleKey(KeyEventArgs e)
    {
        const double step = 0.07;
        switch (e.Key)
        {
            case Key.Left: Orbit(step, 0); break;
            case Key.Right: Orbit(-step, 0); break;
            case Key.Up when !e.KeyModifiers.HasFlag(KeyModifiers.Shift): Orbit(0, step); break;
            case Key.Down when !e.KeyModifiers.HasFlag(KeyModifiers.Shift): Orbit(0, -step); break;
            case Key.OemPlus or Key.Add or Key.PageUp: Zoom(0.88); break;
            case Key.OemMinus or Key.Subtract or Key.PageDown: Zoom(1 / 0.88); break;
            case Key.W: Pan(0, 12); break;
            case Key.S: Pan(0, -12); break;
            case Key.A: Pan(12, 0); break;
            case Key.D: Pan(-12, 0); break;
            case Key.OemOpenBrackets when SectionEnabled: NudgeSection(-1); return;  // reports the height itself
            case Key.OemCloseBrackets when SectionEnabled: NudgeSection(+1); return;
            default: return;
        }
        Report($"key {e.Key}");
    }

    private void Orbit(double yawDelta, double pitchDelta)
    {
        _yaw += yawDelta;
        _pitch = Math.Clamp(_pitch + pitchDelta, -Math.PI / 2 + 0.01, Math.PI / 2 - 0.01);
        RequestNextFrameRendering();
    }

    private void Zoom(double factor)
    {
        _distance = Math.Clamp(_distance * factor, 0.5, CameraMath.MaxOrbitDistance(_sceneBounds));
        RequestNextFrameRendering();
    }

    private void Pan(double dx, double dy)
    {
        var eyeDir = new Vector3d(Math.Cos(_pitch) * Math.Cos(_yaw), Math.Cos(_pitch) * Math.Sin(_yaw), Math.Sin(_pitch));
        var right = eyeDir.Cross(Vector3d.UnitZ).Normalized();
        var up = right.Cross(eyeDir); // eyeDir points from target toward eye, so this is screen-up
        double scale = _distance * 0.0018;
        _target += right * (dx * scale) + up * (dy * scale);
        RequestNextFrameRendering();
    }

}
