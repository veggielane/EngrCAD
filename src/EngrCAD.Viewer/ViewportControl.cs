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
    private int _uSectionEnabled, _uSectionAxis, _uSectionOffset, _uAlpha;
    private uint _lineProgram;
    private int _uLineModel, _uLineView, _uLineProj, _uLineColor;
    private int _uLineSectionEnabled, _uLineSectionAxis, _uLineSectionOffset;
    private uint _pointProgram;
    private int _uPointModel, _uPointView, _uPointProj, _uPointColor, _uPointSize;
    private int _uPointSectionEnabled, _uPointSectionAxis, _uPointSectionOffset;
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
    private (IReadOnlyList<PartInstance> Instances, bool Frame, IReadOnlyList<bool>? Visible)? _pending;

    // The view-cube orientation widget (top-right overlay); all cube logic lives in
    // ViewCube.cs — this control only steps its animation, draws it after the scene,
    // and routes clicks inside its region to it (see the ViewCube hooks below).
    private readonly ViewCube _viewCube = new();

    private double _yaw = 0.7;
    private double _pitch = 0.45;
    private double _distance = 15.0;
    private Vector3d _target = (0, 1.6, 0.2);
    private Aabb _sceneBounds = Aabb.Empty;   // all parts' world bounds, for frustum/zoom scaling
    private Point _lastPointer;
    private Point _pressPointer;
    private int _selected = -1;
    private int _hovered = -1;                          // hover highlight (see UpdateHover)
    private HoverThrottle _hoverThrottle = new(4.0);    // re-pick every 4+ DIPs of travel

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

    // Section-plane SDF isolines (self-contained in SectionContours.cs; this class
    // only invalidates, draws, and releases it).
    private readonly SectionContourRenderer _sectionContours = new();

    // 3D annotations (PMI) overlay + measure tool (self-contained in
    // AnnotationLayer.cs; this class feeds items, draws, and releases it).
    private readonly AnnotationLayer _annotations = new();

    // Construction-tree preview overlay (self-contained in PreviewLayer.cs; this class
    // feeds one line batch, draws, and releases it).
    private readonly PreviewLayer _preview = new();

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
    /// <para><paramref name="visible"/> seeds per-instance visibility with the list
    /// (null = all visible). It travels WITH the instances rather than through later
    /// <see cref="SetVisible"/> calls because the swap happens on the render thread:
    /// a host that remembers hidden rows would otherwise write them into the outgoing
    /// list and have them wiped when the new one lands.</para>
    /// </summary>
    public void SetInstances(
        IReadOnlyList<PartInstance> instances, bool frame, IReadOnlyList<bool>? visible = null)
    {
        lock (_sceneLock)
            _pending = (instances, frame, visible);
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
            _pitch = Math.Clamp(value.Pitch, -ViewCubeMath.PitchLimit, ViewCubeMath.PitchLimit);
            _distance = Math.Clamp(value.Distance, 0.5, CameraMath.MaxOrbitDistance(_sceneBounds));
            _target = value.Target;
            lock (_sceneLock)
            {
                // An explicit pose wins over any auto-framing still queued.
                if (_pending is { } pending)
                    _pending = (pending.Instances, false, pending.Visible);
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
        _uSectionAxis = _gl.GetUniformLocation(_program, "uSectionAxis");
        _uSectionOffset = _gl.GetUniformLocation(_program, "uSectionOffset");
        _uAlpha = _gl.GetUniformLocation(_program, "uAlpha");

        _lineProgram = CompileLineProgram(_gl);
        _uLineModel = _gl.GetUniformLocation(_lineProgram, "uModel");
        _uLineView = _gl.GetUniformLocation(_lineProgram, "uView");
        _uLineProj = _gl.GetUniformLocation(_lineProgram, "uProj");
        _uLineColor = _gl.GetUniformLocation(_lineProgram, "uColor");
        _uLineSectionEnabled = _gl.GetUniformLocation(_lineProgram, "uSectionEnabled");
        _uLineSectionAxis = _gl.GetUniformLocation(_lineProgram, "uSectionAxis");
        _uLineSectionOffset = _gl.GetUniformLocation(_lineProgram, "uSectionOffset");

        _pointProgram = CompilePointProgram(_gl);
        _uPointModel = _gl.GetUniformLocation(_pointProgram, "uModel");
        _uPointView = _gl.GetUniformLocation(_pointProgram, "uView");
        _uPointProj = _gl.GetUniformLocation(_pointProgram, "uProj");
        _uPointColor = _gl.GetUniformLocation(_pointProgram, "uColor");
        _uPointSize = _gl.GetUniformLocation(_pointProgram, "uPointSize");
        _uPointSectionEnabled = _gl.GetUniformLocation(_pointProgram, "uSectionEnabled");
        _uPointSectionAxis = _gl.GetUniformLocation(_pointProgram, "uSectionAxis");
        _uPointSectionOffset = _gl.GetUniformLocation(_pointProgram, "uSectionOffset");
        // Desktop GL ignores gl_PointSize unless program point size is enabled; the
        // cap does not exist under GLES (it is always on), where enabling it errors.
        if (GlVersion.Type != GlProfileType.OpenGLES)
            _gl.Enable(EnableCap.ProgramPointSize);

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
    private void ApplyInstances(
        GL gl, IReadOnlyList<PartInstance> instances, bool frame, IReadOnlyList<bool>? visible)
    {
        DeleteMeshBuffers(gl);
        lock (_sceneLock)
        {
            _meshes.Clear();
            _pickData.Clear();
            _visible.Clear();
            _instances.Clear();
            _selected = -1;
            _hovered = -1;
            _hoverThrottle.Reset();

            var shared = new Dictionary<Part, SharedMesh>(); // Part has reference identity
            var bounds = Aabb.Empty;
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
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
                _visible.Add(visible is null || i >= visible.Count || visible[i]);
                _instances.Add(instance);
                bounds = bounds.Union(worldBounds);
            }

            RebuildGrid(gl, bounds);
            _sceneBounds = bounds;
            _sectionContours.Invalidate();   // new scene: cached SDF routes are stale
            LoadAnnotations(report: true);
            ClearMeasurement();              // measure points reference the old scene
            _preview.Set(null, Matrix4d.Identity);   // preview referenced the old scene

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
        // B-Rep-backed parts get their edge overlay from the ACTUAL B-Rep edges
        // (exact circles stay smooth at coarse tessellation); others fall back to
        // mesh dihedrals. Cached per part — Scene.PreMesh primed it off-thread.
        var featureEdges = part.GetFeatureEdges();
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
        _sectionContours.Release(_gl);
        _annotations.Release(_gl);
        _preview.Release(_gl);
        _meshes.Clear();
        if (_gridVbo != 0)
        {
            _gl.DeleteBuffer(_gridVbo);
            _gl.DeleteVertexArray(_gridVao);
            _gl.DeleteBuffer(_axesVbo);
            _gl.DeleteVertexArray(_axesVao);
            _gridVbo = 0;
        }
        _viewCube.DeleteResources(_gl);
        _gl.DeleteVertexArray(_bgVao);
        _gl.DeleteProgram(_program);
        _gl.DeleteProgram(_lineProgram);
        _gl.DeleteProgram(_pointProgram);
        _gl.DeleteProgram(_bgProgram);
        _gl.Dispose();
        _gl = null;
    }

    protected override unsafe void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl is null)
            return;
        var gl = _gl;

        (IReadOnlyList<PartInstance> Instances, bool Frame, IReadOnlyList<bool>? Visible)? update = null;
        lock (_sceneLock)
        {
            if (_pending is not null)
            {
                update = _pending;
                _pending = null;
            }
        }
        if (update is { } u)
            ApplyInstances(gl, u.Instances, u.Frame, u.Visible);

        // View-cube hook 1: a click on the cube armed a pose animation — advance the
        // orbit camera toward it and keep frames coming until it lands.
        if (_viewCube.Step(out double animatedYaw, out double animatedPitch))
        {
            _yaw = animatedYaw;
            _pitch = animatedPitch;
            if (_viewCube.Animating)
                RequestNextFrameRendering();
        }

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
        var sectionAxis = _sectionAxis.Direction();
        gl.Uniform1(_uSectionEnabled, _sectionEnabled ? 1f : 0f);
        gl.Uniform3(_uSectionAxis, (float)sectionAxis.X, (float)sectionAxis.Y, (float)sectionAxis.Z);
        gl.Uniform1(_uSectionOffset, (float)_sectionOffset);

        // Section mode relies on face culling staying OFF (the GL default; nothing here
        // enables CullFace): clipping a closed solid exposes its interior, and the
        // fragment shader shades those backfaces as cut material via gl_FrontFacing.
        // Fills push back slightly (polygon offset) so the edge overlay wins depth.
        gl.Uniform1(_uAlpha, 1f);
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1f, 1f);
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i])
                continue;
            var em = EffectiveModeOf(i);
            if (em is EffectiveMode.Shaded or EffectiveMode.ShadedWithEdges)
                DrawFill(gl, i, matrix);
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        // Line overlay: feature edges for shaded-with-edges parts (the CAD look),
        // full triangle wireframe for wireframe parts. Lines belong to the model, so
        // the section plane clips them consistently with the fills.
        gl.UseProgram(_lineProgram);
        gl.Uniform1(_uLineSectionEnabled, _sectionEnabled ? 1f : 0f);
        gl.Uniform3(_uLineSectionAxis, (float)sectionAxis.X, (float)sectionAxis.Y, (float)sectionAxis.Z);
        gl.Uniform1(_uLineSectionOffset, (float)_sectionOffset);
        bool anyPoints = false;
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i])
                continue;
            var m = _meshes[i];
            switch (EffectiveModeOf(i))
            {
                case EffectiveMode.Wireframe when m.WireVertexCount > 0:
                    CameraMath.WriteColumnMajor(m.Model, matrix);
                    gl.UniformMatrix4(_uLineModel, 1, false, matrix);
                    var wireColor = HighlightedColor(i, m.Color);
                    gl.Uniform3(_uLineColor, wireColor.R, wireColor.G, wireColor.B);
                    gl.BindVertexArray(m.WireVao);
                    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)m.WireVertexCount);
                    break;
                case EffectiveMode.ShadedWithEdges when m.EdgeVertexCount > 0:
                    DrawFeatureEdges(gl, i, matrix);
                    break;
                case EffectiveMode.Points:
                    anyPoints = true;
                    break;
            }
        }

        // Points pass: vertex point sprites (drawn as indexed points over the same
        // mesh buffers — a flat RenderMesh references each vertex exactly once).
        if (anyPoints)
        {
            gl.UseProgram(_pointProgram);
            CameraMath.WriteColumnMajor(view, matrix);
            gl.UniformMatrix4(_uPointView, 1, false, matrix);
            CameraMath.WriteColumnMajor(proj, matrix);
            gl.UniformMatrix4(_uPointProj, 1, false, matrix);
            gl.Uniform1(_uPointSize, 4f * (float)scaling);
            gl.Uniform1(_uPointSectionEnabled, _sectionEnabled ? 1f : 0f);
            gl.Uniform3(_uPointSectionAxis, (float)sectionAxis.X, (float)sectionAxis.Y, (float)sectionAxis.Z);
            gl.Uniform1(_uPointSectionOffset, (float)_sectionOffset);
            for (int i = 0; i < _meshes.Count; i++)
            {
                if (!_visible[i] || EffectiveModeOf(i) != EffectiveMode.Points)
                    continue;
                var m = _meshes[i];
                CameraMath.WriteColumnMajor(m.Model, matrix);
                gl.UniformMatrix4(_uPointModel, 1, false, matrix);
                var pointColor = HighlightedColor(i, m.Color);
                gl.Uniform3(_uPointColor, pointColor.R, pointColor.G, pointColor.B);
                gl.BindVertexArray(m.Vao);
                gl.DrawElements(PrimitiveType.Points, (uint)m.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
        }

        // Translucent pass, after everything opaque: blended fills back-to-front by
        // part center, with depth writes off so translucent parts do not occlude each
        // other; depth *testing* stays on so opaque geometry in front still wins.
        // Order/depth scratch buffers are reused frame to frame (render paths must not
        // allocate per frame); the sort (RenderModes.SortBackToFront, shared with the
        // offscreen pass) creates no comparer delegate either.
        if (_translucentOrder.Length < _meshes.Count)
        {
            _translucentOrder = new int[_meshes.Count];
            _translucentDepth = new double[_meshes.Count];
        }
        int translucentCount = 0;
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (_visible[i] && EffectiveModeOf(i) == EffectiveMode.Translucent)
            {
                _translucentOrder[translucentCount] = i;
                _translucentDepth[translucentCount] = (_meshes[i].WorldCenter - eye).LengthSquared;
                translucentCount++;
            }
        }
        if (translucentCount > 0)
        {
            RenderModes.SortBackToFront(_translucentOrder, _translucentDepth, translucentCount);

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
        // Section-plane SDF isolines, after everything else so they read as an
        // overlay on the cut (depth test still applies; the polygon-offset fills
        // lose to coincident lines, same as feature edges).
        if (_sectionEnabled)
            DrawSectionContours(gl, matrix);

        // Annotation overlay (PMI): dimensions/notes/datums + the measure tool's
        // transient dimension, always on top (depth-off pass inside the layer) and
        // never section-clipped; before the view cube so the cube stays the topmost
        // chrome. Billboard geometry rebuilds only when the camera pose, viewport,
        // or annotation set changes (value-equality cache in the layer).
        DrawAnnotations(gl, height, scaling, matrix);

        // Construction-tree preview: the selected build step's geometry (a sketch on
        // its plane, or an intermediate operation's edges) drawn over the model, so a
        // rollback view reads against the finished part.
        _preview.Draw(gl, _lineProgram, _uLineModel, _uLineColor, _uLineSectionEnabled, matrix);
        gl.BindVertexArray(0);

        // View-cube hook 2: the orientation widget draws last, over everything, into
        // its own top-right sub-viewport (depth cleared, ortho mini-projection); it
        // reuses the line program, so it precedes the screenshot readback and appears
        // in saved captures like the rest of the window content.
        DrawViewCube(gl, width, height, scaling);

        // A requested screenshot reads back the finished frame while the context is
        // current (GL calls are only legal here), then encodes off-thread.
        if (Interlocked.Exchange(ref _pendingScreenshot, null) is { } screenshotPath)
            CaptureFramebuffer(gl, (int)width, (int)height, screenshotPath);
    }

    /// <summary>The global-style x part-mode precedence, resolved by
    /// <see cref="RenderModes.Resolve"/> (shared with the offscreen pass).</summary>
    private EffectiveMode EffectiveModeOf(int index) =>
        RenderModes.Resolve(_viewStyle, _instances[index].Part.DisplayMode);

    /// <summary>
    /// Draws SDF iso-distance contours on the section plane for parts with an implicit
    /// route (SDF geometry, or a Shape convertible to implicit — lowered once and
    /// cached). Geometry recomputes only when the plane (axis or offset), scene, or
    /// visibility changes. The plane is passed as its clip rule (axis, offset), which
    /// keeps the renderer plane-general.
    /// </summary>
    private void DrawSectionContours(GL gl, Span<float> matrix) =>
        _sectionContours.Draw(gl, _instances, _visible, _sectionAxis.Direction(), _sectionOffset,
            _lineProgram, _uLineModel, _uLineColor, matrix, _sectionReport ??= Report);

    // Cached delegate so the per-frame contour draw does not allocate.
    private Action<string>? _sectionReport;

    // ---- annotations (PMI) + measure tool (all overlay logic lives in AnnotationLayer.cs) ----

    private bool _showAnnotations = true;
    private bool _measureMode;
    private Vector3d? _measureStart;

    /// <summary>Whether part-attached annotations (dimensions, notes, datums) are
    /// drawn (default true; the measure tool's transient dimension always shows).</summary>
    public bool ShowAnnotations
    {
        get => _showAnnotations;
        set
        {
            _showAnnotations = value;
            RequestNextFrameRendering();
        }
    }

    /// <summary>True when the loaded scene carries any annotations (hosts use it for
    /// toolbar affordances).</summary>
    public bool HasAnnotations => _annotations.HasItems;

    /// <summary>
    /// Measure mode: while on, clicks pick surface points instead of selecting parts —
    /// two picks create a transient point-to-point dimension shown immediately.
    /// Escape clears the measurement; turning the mode off clears and exits.
    /// </summary>
    public bool MeasureMode
    {
        get => _measureMode;
        set
        {
            _measureMode = value;
            if (!value)
                ClearMeasurement();
            else
                Report("measure: click the first point");
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// Runs one measure pick at a control-space position — the exact path measure-mode
    /// clicks take. Public so tests and custom hosts can drive the tool directly
    /// (synthetic mouse input does not reach Avalonia).
    /// </summary>
    public void MeasurePick(Point position)
    {
        int hit = HitTest(position, out var worldPoint);
        if (hit < 0)
        {
            Report("measure: no surface under the cursor");
            return;
        }
        if (_measureStart is not { } start)
        {
            _measureStart = worldPoint;
            _annotations.SetTransient(null);
            Report($"measure: first point ({worldPoint.X:G4}, {worldPoint.Y:G4}, {worldPoint.Z:G4}) - click the second");
        }
        else
        {
            var resolved = new LinearDimension(start, worldPoint).Resolve();
            _annotations.SetTransient(resolved);
            _measureStart = null;
            Report($"measure: {resolved.Text}");
        }
        RequestNextFrameRendering();
    }

    /// <summary>Clears the in-progress measure point and the transient dimension.</summary>
    private void ClearMeasurement()
    {
        _measureStart = null;
        if (_annotations.HasTransient)
        {
            _annotations.SetTransient(null);
            RequestNextFrameRendering();
        }
    }

    /// <summary>Collects the VISIBLE instances' resolved annotations for the overlay
    /// (pre-resolved off-thread by <c>Scene.PreMesh</c>; per-part failures surface in
    /// the status overlay instead of killing the scene). Re-run when visibility
    /// changes: hiding a part must hide its dimensions too, otherwise they float in
    /// empty space. Caller holds <see cref="_sceneLock"/>.</summary>
    private void LoadAnnotations(bool report)
    {
        var items = new List<AnnotationItem>();
        for (int i = 0; i < _instances.Count; i++)
        {
            var instance = _instances[i];
            if (!_visible[i] || instance.Part.Annotations.Count == 0)
                continue;
            if (instance.Part.TryResolveAnnotations(out var resolved, out string? error))
            {
                foreach (var annotation in resolved)
                    items.Add(new AnnotationItem(annotation, instance.World));
            }
            else if (report && error is not null)
                ShowStatus(error);
        }
        _annotations.SetItems(items);
    }

    /// <summary>Draws the annotation overlay (end of the render pass, before the view
    /// cube). The layer caches by camera value-equality, so a static view costs one
    /// struct comparison.</summary>
    private void DrawAnnotations(GL gl, uint heightPx, double scaling, Span<float> matrix)
    {
        var camera = AnnotationCamera.From(
            new CameraState(_yaw, _pitch, _distance, _target), _orthographic, heightPx, scaling);
        _annotations.Draw(gl, camera, _showAnnotations,
            _lineProgram, _uLineModel, _uLineColor, _uLineSectionEnabled, matrix);
    }

    // ---- construction-tree preview (all overlay logic lives in PreviewLayer.cs) ----

    /// <summary>
    /// Shows the geometry of one construction-tree row over the model — a sketch drawn
    /// on its plane, or an intermediate operation's feature edges (the rollback view) —
    /// posed by <paramref name="world"/> (the owning instance's placement). Pass null
    /// to clear. Thread-safe; the batch uploads inside the next render pass.
    /// <para>The segments must be built OFF the UI/render thread (lowering a sub-shape
    /// tessellates) — see <c>ConstructionPreviewCache</c> in EngrCAD.Modeling.</para>
    /// </summary>
    public void SetConstructionPreview(
        IReadOnlyList<(Vector3d A, Vector3d B)>? segments, Matrix4d? world = null)
    {
        _preview.Set(segments, world ?? Matrix4d.Identity);
        RequestNextFrameRendering();
    }

    /// <summary>Whether a construction preview is currently showing.</summary>
    public bool HasConstructionPreview => _preview.HasPreview;

    // ---- view cube hooks (all cube logic lives in ViewCube.cs) ----

    /// <summary>Draws the orientation widget at the end of the render pass.</summary>
    private void DrawViewCube(GL gl, uint width, uint height, double scaling) =>
        _viewCube.Draw(gl, width, height, scaling, _yaw, _pitch, new LineProgramHandles(
            _lineProgram, _uLineModel, _uLineView, _uLineProj, _uLineColor, _uLineSectionEnabled));

    /// <summary>
    /// Routes a click at a control-space position to the view cube. Returns true when
    /// the position lies inside the cube's top-right region (the region claims the
    /// click, so parts behind the widget are never picked through it); a hit on a cube
    /// face, edge, or corner animates the camera to that standard view (~250 ms eased,
    /// shortest yaw path, distance and target kept). Called by the window input
    /// handler before scene picking; public so tests and custom hosts can invoke the
    /// pick path directly (synthetic mouse input does not reach Avalonia).
    /// </summary>
    public bool ViewCubeClick(Point position)
    {
        if (!_viewCube.HandleClick(position.X, position.Y, Bounds.Width, Bounds.Height,
                _yaw, _pitch, out string? view))
            return false;
        if (view is not null)
        {
            Report($"view: {view}");
            RequestNextFrameRendering();
        }
        return true;
    }

    // ---- hover highlight (view cube faces + model parts) ----

    /// <summary>Index of the hovered part, −1 for none (the fainter pre-selection
    /// tint; a hovered selected part shows only the selection).</summary>
    public int Hovered => _hovered;

    /// <summary>
    /// Runs the hover update for a control-space position — the exact path pointer
    /// moves take (cube region first, then the throttled part raycast). Public so
    /// tests and custom hosts can drive hover directly (synthetic mouse input does
    /// not reach Avalonia).
    /// </summary>
    public void HoverAt(Point position) => UpdateHover(position);

    /// <summary>Pointer-move hover update: over the cube region the widget highlights
    /// its face/edge/corner and model hover is suppressed; elsewhere the part under
    /// the cursor is re-picked (throttled to 4+ DIPs of travel) and tinted. Redraws
    /// only when a hover state actually changes. Like click picking, hover ignores
    /// the section plane (v1) — it can hover a part through the cut-away half.</summary>
    private void UpdateHover(Point pos)
    {
        // Window-level handlers see moves over the whole window; outside the
        // viewport there is nothing to hover.
        if (pos.X < 0 || pos.Y < 0 || pos.X > Bounds.Width || pos.Y > Bounds.Height)
        {
            ClearHover();
            return;
        }

        bool insideCube = _viewCube.UpdateHover(
            pos.X, pos.Y, Bounds.Width, Bounds.Height, _yaw, _pitch, out bool cubeChanged);
        if (cubeChanged)
            RequestNextFrameRendering();
        if (insideCube)
        {
            SetHovered(-1);
            _hoverThrottle.Reset();   // leaving the widget re-picks immediately
            return;
        }

        if (!_hoverThrottle.ShouldSample(pos.X, pos.Y))
            return;
        SetHovered(HitTest(pos));
    }

    private void SetHovered(int index)
    {
        if (index == _hovered)
            return;
        _hovered = index;
        if (index >= 0)
            Report($"hover '{_instances[index].Path}'");   // the pre-selection affordance
        RequestNextFrameRendering();
    }

    /// <summary>Clears both hover highlights (a drag/press started or the pointer
    /// left the viewport).</summary>
    private void ClearHover()
    {
        if (_viewCube.ClearHover())
            RequestNextFrameRendering();
        SetHovered(-1);
        _hoverThrottle.Reset();
    }

    /// <summary>Line/point color for a part: selection gold wins, hover blends the
    /// part color 35% toward it, otherwise the part's own color.</summary>
    private (float R, float G, float B) HighlightedColor(int index, (float R, float G, float B) color) =>
        index == _selected ? (1.0f, 0.85f, 0.35f)
        : index == _hovered
            ? (color.R + (1.0f - color.R) * 0.35f, color.G + (0.85f - color.G) * 0.35f,
                color.B + (0.35f - color.B) * 0.35f)
            : color;

    private unsafe void DrawFill(GL gl, int index, Span<float> matrix)
    {
        var m = _meshes[index];
        CameraMath.WriteColumnMajor(m.Model, matrix);
        gl.UniformMatrix4(_uModel, 1, false, matrix);
        gl.Uniform3(_uColor, m.Color.R, m.Color.G, m.Color.B);
        // One shader knob for both states: 1.0 = selection gold, 0.35 = the fainter
        // hover tint; a hovered selected part just shows selection.
        gl.Uniform1(_uHighlight, index == _selected ? 1f : index == _hovered ? 0.35f : 0f);
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
        int best = HitTest(pixel);
        Select(best == _selected ? -1 : best); // clicking the selection clears it
        Report(_selected >= 0 ? $"picked '{_instances[_selected].Path}'" : "picked nothing");
        SelectionChanged?.Invoke(_selected);
    }

    // BVH candidate scratch reused across HitTest calls (hover re-picks on pointer
    // moves — no allocation there; UI-thread only, like all input handling).
    private readonly List<int> _hitScratch = [];

    /// <summary>The nearest visible part under a control-space position (−1 for none):
    /// unprojected ray + per-part BVH + Möller–Trumbore. Shared by click picking, the
    /// hover highlight, and the measure tool. Honors the section plane: surfaces the
    /// cut removed cannot be picked through (<see cref="SectionClip"/> applies the
    /// shaders' own discard rule).</summary>
    private int HitTest(Point pixel) => HitTest(pixel, out _);

    /// <summary><see cref="HitTest(Point)"/> that also reports the world-space hit
    /// point (the measure tool's pick).</summary>
    private int HitTest(Point pixel, out Vector3d worldPoint)
    {
        worldPoint = default;
        double width = Math.Max(1, Bounds.Width);
        double height = Math.Max(1, Bounds.Height);
        var eye = CameraMath.Eye(_yaw, _pitch, _distance, _target);
        var viewProjection = ProjectionMatrix(width / height)
                           * CameraMath.LookAt(eye, _target, Vector3d.UnitZ);
        if (!viewProjection.TryInvert(out var unproject))
            return -1;

        double ndcX = 2 * pixel.X / width - 1;
        double ndcY = 1 - 2 * pixel.Y / height;
        var nearPoint = unproject.TransformPoint((ndcX, ndcY, -1));
        var farPoint = unproject.TransformPoint((ndcX, ndcY, 1));

        int best = -1;
        double bestT = double.PositiveInfinity;
        var hits = _hitScratch;
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
                    // World-space hit point (local ray back through the instance's
                    // model matrix): the measure tool's pick, and the section test.
                    var world = _meshes[i].Model.TransformPoint(origin + direction * t);
                    // A surface the section plane clipped away is not there to click:
                    // skip it and keep looking, so the ray lands on the interior the
                    // cut exposed rather than the removed shell.
                    if (SectionClip.Hides(_sectionEnabled, world, _sectionAxis, _sectionOffset))
                        continue;
                    bestT = t;
                    best = i;
                    worldPoint = world;
                }
            }
        }
        return best;
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

    /// <summary>Shows or hides a part (index into the current part list). A hidden
    /// part's 3D annotations are hidden with it.</summary>
    public void SetVisible(int index, bool visible)
    {
        lock (_sceneLock)
        {
            if (index < 0 || index >= _visible.Count || _visible[index] == visible)
                return;
            _visible[index] = visible;
            LoadAnnotations(report: false);   // dimensions follow their part's visibility
        }
        // Isolines need no nudge: SectionContourRenderer already detects the changed
        // visibility set itself (and Invalidate would drop its cached SDF lowerings).
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

    private ViewStyle _viewStyle = ViewStyle.ShadedWithEdges;

    /// <summary>
    /// Global view style for the whole viewport (points / wireframe / shaded / shaded
    /// with edges; default <see cref="ViewStyle.ShadedWithEdges"/>). Sets how parts
    /// render by default; a part with an explicitly non-default
    /// <see cref="Part.DisplayMode"/> (Wireframe or Translucent) keeps its own mode —
    /// see <see cref="Viewer.ViewStyle"/> for the precedence rule.
    /// </summary>
    public ViewStyle ViewStyle
    {
        get => _viewStyle;
        set
        {
            _viewStyle = value;
            RequestNextFrameRendering();
        }
    }

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
    private SectionAxis _sectionAxis = SectionAxis.Z;
    private double _sectionOffset;
    private bool _sectionOffsetSet;

    /// <summary>
    /// Section mode: clips the model at an axis-aligned plane
    /// (dot(world, <see cref="SectionAxis"/>) = <see cref="SectionOffset"/>) to reveal
    /// interiors — exposed backfaces render as flat cut material. When first enabled,
    /// the offset defaults to the middle of the current parts' bounds along the axis.
    /// Picking ignores the section plane (v1).
    /// </summary>
    public bool SectionEnabled
    {
        get => _sectionEnabled;
        set
        {
            _sectionEnabled = value;
            if (value && !_sectionOffsetSet)
                ResetSectionOffset();
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// Which world axis the section plane is perpendicular to (default Z — the classic
    /// horizontal section). Changing the axis re-centers the plane at the middle of the
    /// parts' bounds along the new axis (one offset is meaningless on another axis).
    /// </summary>
    public SectionAxis SectionAxis
    {
        get => _sectionAxis;
        set
        {
            if (_sectionAxis != value)
            {
                _sectionAxis = value;
                ResetSectionOffset();
            }
            RequestNextFrameRendering();
        }
    }

    /// <summary>Plane offset along <see cref="SectionAxis"/>; geometry beyond it
    /// (larger axis coordinate) is clipped.</summary>
    public double SectionOffset
    {
        get => _sectionOffset;
        set
        {
            _sectionOffset = value;
            _sectionOffsetSet = true;
            RequestNextFrameRendering();
        }
    }

    /// <summary>Legacy name for <see cref="SectionOffset"/> from when section planes
    /// were Z-only (kept so existing hosts compile; identical semantics when
    /// <see cref="SectionAxis"/> is Z, the default).</summary>
    public double SectionHeight
    {
        get => SectionOffset;
        set => SectionOffset = value;
    }

    /// <summary>Centers the section plane in the parts' bounds along the active axis.</summary>
    private void ResetSectionOffset()
    {
        var bounds = PartsBounds();
        if (bounds.IsEmpty)
            return;
        _sectionOffset = AxisComponent(bounds.Center, _sectionAxis);
        _sectionOffsetSet = true;
    }

    private static double AxisComponent(in Vector3d v, SectionAxis axis) => axis switch
    {
        SectionAxis.X => v.X,
        SectionAxis.Y => v.Y,
        _ => v.Z,
    };

    /// <summary>Moves the section plane by 2% of the parts' extent along the active
    /// axis per step.</summary>
    private void NudgeSection(int direction)
    {
        var bounds = PartsBounds();
        double extent = bounds.IsEmpty ? 10 : AxisComponent(bounds.Size, _sectionAxis);
        SectionOffset += direction * extent * 0.02;
        Report($"section {_sectionAxis.ToString().ToLowerInvariant()} = {_sectionOffset:G4}");
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

    private uint CompilePointProgram(GL gl) => ViewerShaders.LinkProgram(
        gl, ShaderHeader + ViewerShaders.PointVertex, ShaderHeader + ViewerShaders.PointFragment,
        bindAttributes: true);

    private uint CompileBackgroundProgram(GL gl) => ViewerShaders.LinkProgram(
        gl, ShaderHeader + ViewerShaders.BackgroundVertex, ShaderHeader + ViewerShaders.BackgroundFragment,
        bindAttributes: false);

    // ---- camera ----

    private void HandlePressed(PointerPressedEventArgs e)
    {
        _lastPointer = e.GetPosition(this);
        _pressPointer = _lastPointer;
        // Remember whether the gesture began on the view cube: a drag that started
        // there rotate-snaps on release (commercial-cube behavior), while a drag
        // starting anywhere else orbits freely.
        _pressedOnCube = ViewCube.InRegion(_pressPointer.X, _pressPointer.Y, Bounds.Width, Bounds.Height);
        ClearHover();   // a press starts a click or drag; hover returns on the next move
        Report($"press at {_pressPointer.X:F0},{_pressPointer.Y:F0}");
    }

    private bool _pressedOnCube;

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
        else
            UpdateHover(pos);   // no buttons: hover highlight (cube region or part)
    }

    private void HandleReleased(PointerReleasedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var moved = pos - _pressPointer;
        // Click-vs-drag discrimination: a release within 4 DIPs of the press (16 =
        // 4 DIP squared, compared against the squared travel to skip the sqrt) is a
        // click that picks; anything farther was an orbit/pan/zoom drag. A UI feel
        // threshold matching HoverThrottle's 4-DIP re-pick distance — not a Tolerance
        // decision, and DIP-based so it is monitor-scale independent.
        if (moved.X * moved.X + moved.Y * moved.Y < 16)
        {
            // View-cube pre-check: clicks inside the cube region go to the widget
            // (view change), never through it to scene picking. Drags are untouched —
            // dragging on the cube orbits via the normal orbit handling above. In
            // measure mode, clicks pick surface points instead of selecting parts.
            if (!ViewCubeClick(pos))
            {
                if (_measureMode)
                    MeasurePick(pos);
                else
                    Pick(pos);
            }
        }
        else if (_pressedOnCube)
            SnapViewCube();   // dragging ON the cube settles onto a standard view
        else
            Report("release (drag end)");
        _pressedOnCube = false;
    }

    /// <summary>
    /// Rotate-snap: animates the camera to the standard orientation nearest the current
    /// pose (the face/edge/corner you ended up closest to) — what a drag that started
    /// on the view cube does on release. Public so tests and custom hosts can drive it
    /// directly (synthetic mouse input does not reach Avalonia).
    /// </summary>
    public void SnapViewCube()
    {
        Report($"view: snapped to {_viewCube.SnapToNearest(_yaw, _pitch)}");
        RequestNextFrameRendering();
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
            case Key.Escape when _measureMode:
                ClearMeasurement();
                Report("measure: cleared");
                return;
            default: return;
        }
        Report($"key {e.Key}");
    }

    private void Orbit(double yawDelta, double pitchDelta)
    {
        _yaw += yawDelta;
        _pitch = Math.Clamp(_pitch + pitchDelta, -ViewCubeMath.PitchLimit, ViewCubeMath.PitchLimit);
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
