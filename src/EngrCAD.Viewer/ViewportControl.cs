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
    private uint _lineProgram;
    private int _uLineModel, _uLineView, _uLineProj, _uLineColor;
    private uint _bgProgram, _bgVao;
    private uint _gridVao, _gridVbo;
    private int _gridCount;
    private uint _axesVao, _axesVbo;
    private readonly List<GpuMesh> _meshes = [];
    private readonly List<string> _partNames = [];
    private readonly List<bool> _visible = [];
    private readonly List<Part> _parts = [];

    private readonly object _sceneLock = new();
    private (IReadOnlyList<Part> Parts, bool Frame)? _pending;

    private double _yaw = 0.7;
    private double _pitch = 0.45;
    private double _distance = 15.0;
    private Vector3d _target = (0, 1.6, 0.2);
    private Point _lastPointer;
    private Point _pressPointer;
    private int _selected = -1;

    private readonly record struct GpuMesh(
        uint Vao, uint Vbo, uint Ebo, int IndexCount, Matrix4d Model, (float R, float G, float B) Color,
        uint EdgeVao, uint EdgeVbo, int EdgeVertexCount);

    /// <summary>CPU-side pick data per object: triangles plus a BVH over them, in object space.</summary>
    private sealed record PickData(RenderMesh Mesh, EngrCAD.Core.Spatial.Bvh Bvh);

    private readonly List<PickData> _pickData = [];

    /// <summary>
    /// Replaces the displayed parts (one tab's worth). Thread-safe: geometry is
    /// uploaded on the next rendered frame (the GL context is only current there).
    /// With <paramref name="frame"/> the camera auto-frames to the parts' bounds,
    /// otherwise it is left untouched. Parts should be pre-meshed
    /// (<c>Scene.PreMesh</c>) so no tessellation happens on the render thread.
    /// </summary>
    public void SetParts(IReadOnlyList<Part> parts, bool frame)
    {
        lock (_sceneLock)
            _pending = (parts, frame);
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
            _distance = Math.Clamp(value.Distance, 0.5, 120.0);
            _target = value.Target;
            lock (_sceneLock)
            {
                // An explicit pose wins over any auto-framing still queued.
                if (_pending is { } pending)
                    _pending = (pending.Parts, false);
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

        _lineProgram = CompileLineProgram(_gl);
        _uLineModel = _gl.GetUniformLocation(_lineProgram, "uModel");
        _uLineView = _gl.GetUniformLocation(_lineProgram, "uView");
        _uLineProj = _gl.GetUniformLocation(_lineProgram, "uProj");
        _uLineColor = _gl.GetUniformLocation(_lineProgram, "uColor");

        _bgProgram = CompileBackgroundProgram(_gl);
        _bgVao = _gl.GenVertexArray(); // vertexless fullscreen triangle via gl_VertexID
    }

    /// <summary>Uploads a part list, replacing existing GPU resources. GL context must be current.</summary>
    private void ApplyParts(GL gl, IReadOnlyList<Part> parts, bool frame)
    {
        foreach (var m in _meshes)
        {
            gl.DeleteBuffer(m.Vbo);
            gl.DeleteBuffer(m.Ebo);
            gl.DeleteVertexArray(m.Vao);
            gl.DeleteBuffer(m.EdgeVbo);
            gl.DeleteVertexArray(m.EdgeVao);
        }
        lock (_sceneLock)
        {
            _meshes.Clear();
            _pickData.Clear();
            _partNames.Clear();
            _visible.Clear();
            _parts.Clear();
            _selected = -1;

            var bounds = Aabb.Empty;
            foreach (var part in parts)
            {
                var color = part.Color ?? Palette.Steel;
                var render = RenderMesh.CreateFlat(part.GetMesh());
                _meshes.Add(Upload(gl, render, part.Transform, (color.R, color.G, color.B),
                    MeshFeatureEdges.Extract(part.GetMesh())));
                _partNames.Add(part.Name);
                _visible.Add(true);
                _parts.Add(part);
                bounds = bounds.Union(part.Bounds());

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
                _pickData.Add(new PickData(render, EngrCAD.Core.Spatial.Bvh.Build(boxes)));
            }

            RebuildGrid(gl, bounds);

            if (frame && !bounds.IsEmpty)
            {
                _target = bounds.Center;
                double diagonal = bounds.Size.Length;
                _distance = Math.Clamp(diagonal * 1.25 + 1, 2, 110);
            }
        }
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

        double diagonal = bounds.IsEmpty ? 10 : bounds.Size.Length;
        double spacing = NiceStep(diagonal / 10);
        float extent = (float)(spacing * 12);

        var lines = new List<float>();
        for (int i = -12; i <= 12; i++)
        {
            float p = (float)(i * spacing);
            lines.AddRange([p, -extent, 0, p, extent, 0]);   // parallel to Y
            lines.AddRange([-extent, p, 0, extent, p, 0]);   // parallel to X
        }
        _gridCount = lines.Count / 3;
        (_gridVao, _gridVbo) = UploadLines(gl, [.. lines]);

        float a = extent * 0.6f;
        (_axesVao, _axesVbo) = UploadLines(gl,
        [
            0, 0, 0, a, 0, 0,   // +X
            0, 0, 0, 0, a, 0,   // +Y
            0, 0, 0, 0, 0, a,   // +Z
        ]);
    }

    private static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
        double residual = raw / magnitude;
        return magnitude * (residual < 1.5 ? 1 : residual < 3.5 ? 2 : residual < 7.5 ? 5 : 10);
    }

    private static unsafe (uint Vao, uint Vbo) UploadLines(GL gl, float[] vertices)
    {
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
        return (vao, vbo);
    }

    private static Vector3d PickVertex(RenderMesh mesh, uint index) => new(
        mesh.Positions[index * 3],
        mesh.Positions[index * 3 + 1],
        mesh.Positions[index * 3 + 2]);

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is null)
            return;
        foreach (var m in _meshes)
        {
            _gl.DeleteBuffer(m.Vbo);
            _gl.DeleteBuffer(m.Ebo);
            _gl.DeleteVertexArray(m.Vao);
            _gl.DeleteBuffer(m.EdgeVbo);
            _gl.DeleteVertexArray(m.EdgeVao);
        }
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

        (IReadOnlyList<Part> Parts, bool Frame)? update = null;
        lock (_sceneLock)
        {
            if (_pending is not null)
            {
                update = _pending;
                _pending = null;
            }
        }
        if (update is { } u)
            ApplyParts(gl, u.Parts, u.Frame);

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

        var eye = _target + new Vector3d(
            _distance * Math.Cos(_pitch) * Math.Cos(_yaw),
            _distance * Math.Cos(_pitch) * Math.Sin(_yaw),
            _distance * Math.Sin(_pitch));
        var view = LookAt(eye, _target, Vector3d.UnitZ);
        var proj = ProjectionMatrix((double)width / height);

        Span<float> matrix = stackalloc float[16];

        // Ground grid + world axes.
        gl.UseProgram(_lineProgram);
        WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(_uLineView, 1, false, matrix);
        WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(_uLineProj, 1, false, matrix);
        WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(_uLineModel, 1, false, matrix);
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
        WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(_uView, 1, false, matrix);
        WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(_uProj, 1, false, matrix);
        var lightDir = new Vector3d(-0.5, -0.7, -0.9).Normalized();
        gl.Uniform3(_uLightDir, (float)lightDir.X, (float)lightDir.Y, (float)lightDir.Z);
        gl.Uniform3(_uEyePos, (float)eye.X, (float)eye.Y, (float)eye.Z);

        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1f, 1f);
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i])
                continue;
            var m = _meshes[i];
            WriteColumnMajor(m.Model, matrix);
            gl.UniformMatrix4(_uModel, 1, false, matrix);
            gl.Uniform3(_uColor, m.Color.R, m.Color.G, m.Color.B);
            gl.Uniform1(_uHighlight, i == _selected ? 1f : 0f);
            gl.BindVertexArray(m.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)m.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        // Feature-edge overlay: the shaded-with-edges CAD look.
        gl.UseProgram(_lineProgram);
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i] || _meshes[i].EdgeVertexCount == 0)
                continue;
            var m = _meshes[i];
            WriteColumnMajor(m.Model, matrix);
            gl.UniformMatrix4(_uLineModel, 1, false, matrix);
            if (i == _selected)
                gl.Uniform3(_uLineColor, 1.0f, 0.85f, 0.35f);
            else
                gl.Uniform3(_uLineColor, 0.09f, 0.10f, 0.11f);
            gl.BindVertexArray(m.EdgeVao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)m.EdgeVertexCount);
        }
        gl.BindVertexArray(0);
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
        var eye = _target + new Vector3d(
            _distance * Math.Cos(_pitch) * Math.Cos(_yaw),
            _distance * Math.Cos(_pitch) * Math.Sin(_yaw),
            _distance * Math.Sin(_pitch));
        var viewProjection = ProjectionMatrix(width / height)
                           * LookAt(eye, _target, Vector3d.UnitZ);
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
        Report(_selected >= 0 ? $"picked '{_partNames[_selected]}'" : "picked nothing");
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
        _selected = index >= 0 && index < _partNames.Count ? index : -1;
        string? name = _selected >= 0 ? _partNames[_selected] : null;
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

    /// <summary>Zoom-to-fit: frames the camera on the visible parts' bounds.</summary>
    public void Frame()
    {
        var bounds = Aabb.Empty;
        lock (_sceneLock)
        {
            for (int i = 0; i < _parts.Count; i++)
            {
                if (_visible[i])
                    bounds = bounds.Union(_parts[i].Bounds());
            }
        }
        if (!bounds.IsEmpty)
        {
            _target = bounds.Center;
            _distance = Math.Clamp(bounds.Size.Length * 1.25 + 1, 2, 110);
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

    private Matrix4d ProjectionMatrix(double aspect) => _orthographic
        ? OrthographicMatrix(_distance * Math.Tan(Math.PI / 8), aspect, 0.1, 200.0)
        : Perspective(Math.PI / 4, aspect, 0.1, 200.0);

    /// <summary>Möller–Trumbore; t is in units of the (unnormalized) ray direction.</summary>
    private static bool RayTriangle(in Ray3d ray, in Vector3d a, in Vector3d b, in Vector3d c, out double t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = ray.Direction.Cross(e2);
        double determinant = e1.Dot(p);
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
        return t > 1e-9;
    }

    private unsafe GpuMesh Upload(
        GL gl, RenderMesh mesh, in Matrix4d model, (float, float, float) color,
        List<(Vector3d A, Vector3d B)> featureEdges)
    {
        var edgeVertices = new float[featureEdges.Count * 6];
        for (int i = 0; i < featureEdges.Count; i++)
        {
            var (a, b) = featureEdges[i];
            edgeVertices[i * 6 + 0] = (float)a.X;
            edgeVertices[i * 6 + 1] = (float)a.Y;
            edgeVertices[i * 6 + 2] = (float)a.Z;
            edgeVertices[i * 6 + 3] = (float)b.X;
            edgeVertices[i * 6 + 4] = (float)b.Y;
            edgeVertices[i * 6 + 5] = (float)b.Z;
        }
        var (edgeVao, edgeVbo) = UploadLines(gl, edgeVertices);
        // Interleave position + normal.
        var interleaved = new float[mesh.VertexCount * 6];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            interleaved[v * 6 + 0] = mesh.Positions[v * 3 + 0];
            interleaved[v * 6 + 1] = mesh.Positions[v * 3 + 1];
            interleaved[v * 6 + 2] = mesh.Positions[v * 3 + 2];
            interleaved[v * 6 + 3] = mesh.Normals[v * 3 + 0];
            interleaved[v * 6 + 4] = mesh.Normals[v * 3 + 1];
            interleaved[v * 6 + 5] = mesh.Normals[v * 3 + 2];
        }

        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);

        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, interleaved, BufferUsageARB.StaticDraw);

        uint ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, mesh.Indices, BufferUsageARB.StaticDraw);

        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));

        gl.BindVertexArray(0);
        return new GpuMesh(vao, vbo, ebo, mesh.Indices.Length, model, color, edgeVao, edgeVbo, featureEdges.Count * 2);
    }

    private uint CompileProgram(GL gl)
    {
        bool es = GlVersion.Type == GlProfileType.OpenGLES;
        string header = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";

        string vertexSource = header + """
            in vec3 aPos;
            in vec3 aNormal;
            uniform mat4 uModel;
            uniform mat4 uView;
            uniform mat4 uProj;
            out vec3 vNormal;
            out vec3 vWorldPos;
            void main()
            {
                vec4 world = uModel * vec4(aPos, 1.0);
                vWorldPos = world.xyz;
                vNormal = mat3(uModel) * aNormal;
                gl_Position = uProj * uView * world;
            }
            """;

        string fragmentSource = header + """
            in vec3 vNormal;
            in vec3 vWorldPos;
            uniform vec3 uColor;
            uniform vec3 uLightDir;
            uniform vec3 uEyePos;
            uniform float uHighlight;
            out vec4 fragColor;
            void main()
            {
                vec3 n = normalize(vNormal);
                float diffuse = max(dot(n, -uLightDir), 0.0);
                vec3 v = normalize(uEyePos - vWorldPos);
                vec3 h = normalize(v - uLightDir);
                float specular = pow(max(dot(n, h), 0.0), 48.0) * 0.35;
                vec3 base = mix(uColor, vec3(1.0, 0.85, 0.35), uHighlight * 0.55);
                vec3 c = base * (0.22 + 0.78 * diffuse) + vec3(specular);
                fragColor = vec4(c, 1.0);
            }
            """;

        uint vs = CompileShader(gl, ShaderType.VertexShader, vertexSource);
        uint fs = CompileShader(gl, ShaderType.FragmentShader, fragmentSource);
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.BindAttribLocation(program, 0, "aPos");
        gl.BindAttribLocation(program, 1, "aNormal");
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new InvalidOperationException($"Shader link failed: {gl.GetProgramInfoLog(program)}");
        gl.DetachShader(program, vs);
        gl.DetachShader(program, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return program;
    }

    private uint CompileLineProgram(GL gl)
    {
        bool es = GlVersion.Type == GlProfileType.OpenGLES;
        string header = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";

        string vertexSource = header + """
            in vec3 aPos;
            uniform mat4 uModel;
            uniform mat4 uView;
            uniform mat4 uProj;
            void main() { gl_Position = uProj * uView * uModel * vec4(aPos, 1.0); }
            """;
        string fragmentSource = header + """
            uniform vec3 uColor;
            out vec4 fragColor;
            void main() { fragColor = vec4(uColor, 1.0); }
            """;
        return LinkProgram(gl, vertexSource, fragmentSource, bindAttributes: true);
    }

    private uint CompileBackgroundProgram(GL gl)
    {
        bool es = GlVersion.Type == GlProfileType.OpenGLES;
        string header = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";

        // Vertexless fullscreen triangle; vertical gradient like every CAD package.
        string vertexSource = header + """
            out vec2 vUv;
            void main()
            {
                vec2 corners[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
                vec2 p = corners[gl_VertexID];
                vUv = p * 0.5 + 0.5;
                gl_Position = vec4(p, 0.0, 1.0);
            }
            """;
        string fragmentSource = header + """
            in vec2 vUv;
            out vec4 fragColor;
            void main()
            {
                vec3 bottom = vec3(0.09, 0.10, 0.12);
                vec3 top = vec3(0.18, 0.20, 0.24);
                fragColor = vec4(mix(bottom, top, vUv.y), 1.0);
            }
            """;
        return LinkProgram(gl, vertexSource, fragmentSource, bindAttributes: false);
    }

    private static uint LinkProgram(GL gl, string vertexSource, string fragmentSource, bool bindAttributes)
    {
        uint vs = CompileShader(gl, ShaderType.VertexShader, vertexSource);
        uint fs = CompileShader(gl, ShaderType.FragmentShader, fragmentSource);
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        if (bindAttributes)
            gl.BindAttribLocation(program, 0, "aPos");
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new InvalidOperationException($"Shader link failed: {gl.GetProgramInfoLog(program)}");
        gl.DetachShader(program, vs);
        gl.DetachShader(program, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return program;
    }

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException($"{type} compile failed: {gl.GetShaderInfoLog(shader)}");
        return shader;
    }

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
        _distance = Math.Clamp(_distance * factor, 0.5, 120.0);
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

    // ---- math helpers (rendering-only; kernel math stays in EngrCAD.Core) ----

    private static Matrix4d LookAt(in Vector3d eye, in Vector3d target, in Vector3d up)
    {
        var f = (target - eye).Normalized();
        var r = f.Cross(up).Normalized();
        var u = r.Cross(f);
        return new Matrix4d(
            r.X, r.Y, r.Z, -r.Dot(eye),
            u.X, u.Y, u.Z, -u.Dot(eye),
            -f.X, -f.Y, -f.Z, f.Dot(eye),
            0, 0, 0, 1);
    }

    private static Matrix4d Perspective(double fovY, double aspect, double near, double far)
    {
        double t = 1.0 / Math.Tan(fovY / 2);
        return new Matrix4d(
            t / aspect, 0, 0, 0,
            0, t, 0, 0,
            0, 0, (far + near) / (near - far), 2 * far * near / (near - far),
            0, 0, -1, 0);
    }

    private static Matrix4d OrthographicMatrix(double halfHeight, double aspect, double near, double far) => new(
        1 / (halfHeight * aspect), 0, 0, 0,
        0, 1 / halfHeight, 0, 0,
        0, 0, -2 / (far - near), -(far + near) / (far - near),
        0, 0, 0, 1);

    private static void WriteColumnMajor(in Matrix4d m, Span<float> dst)
    {
        dst[0] = (float)m.M11; dst[1] = (float)m.M21; dst[2] = (float)m.M31; dst[3] = (float)m.M41;
        dst[4] = (float)m.M12; dst[5] = (float)m.M22; dst[6] = (float)m.M32; dst[7] = (float)m.M42;
        dst[8] = (float)m.M13; dst[9] = (float)m.M23; dst[10] = (float)m.M33; dst[11] = (float)m.M43;
        dst[12] = (float)m.M14; dst[13] = (float)m.M24; dst[14] = (float)m.M34; dst[15] = (float)m.M44;
    }
}
