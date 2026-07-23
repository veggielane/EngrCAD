using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Mesh;
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
    private readonly List<GpuMesh> _meshes = [];
    private readonly List<string> _partNames = [];

    private readonly object _sceneLock = new();
    private Scene? _pendingScene;
    private bool _cameraFramed;

    private double _yaw = 0.7;
    private double _pitch = 0.45;
    private double _distance = 15.0;
    private Vector3d _target = (0, 1.6, 0.2);
    private Point _lastPointer;
    private Point _pressPointer;
    private int _selected = -1;

    private readonly record struct GpuMesh(uint Vao, uint Vbo, uint Ebo, int IndexCount, Matrix4d Model, (float R, float G, float B) Color);

    /// <summary>CPU-side pick data per object: triangles plus a BVH over them, in object space.</summary>
    private sealed record PickData(RenderMesh Mesh, EngrCAD.Core.Spatial.Bvh Bvh);

    private readonly List<PickData> _pickData = [];

    /// <summary>
    /// Replaces the displayed scene. Thread-safe: geometry is uploaded on the next
    /// rendered frame (the GL context is only current there). The camera is preserved
    /// across scene updates; the first scene auto-frames it.
    /// </summary>
    public void SetScene(Scene scene)
    {
        lock (_sceneLock)
            _pendingScene = scene;
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
            _cameraFramed = true;
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
    }

    /// <summary>Uploads a scene's parts, replacing existing GPU resources. GL context must be current.</summary>
    private void ApplyScene(GL gl, Scene scene)
    {
        foreach (var m in _meshes)
        {
            gl.DeleteBuffer(m.Vbo);
            gl.DeleteBuffer(m.Ebo);
            gl.DeleteVertexArray(m.Vao);
        }
        _meshes.Clear();
        _pickData.Clear();
        _partNames.Clear();
        _selected = -1;

        foreach (var part in scene.Parts)
        {
            var render = RenderMesh.CreateFlat(part.Mesh);
            _meshes.Add(Upload(gl, render, part.Transform, (part.Color.R, part.Color.G, part.Color.B)));
            _partNames.Add(part.Name);

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

        if (!_cameraFramed)
        {
            var bounds = scene.Bounds();
            if (!bounds.IsEmpty)
            {
                _target = bounds.Center;
                double diagonal = bounds.Size.Length;
                _distance = Math.Clamp(diagonal * 1.25 + 1, 2, 110);
            }
            _cameraFramed = true;
        }
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
        }
        _meshes.Clear();
        _gl.DeleteProgram(_program);
        _gl.Dispose();
        _gl = null;
    }

    protected override unsafe void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl is null)
            return;
        var gl = _gl;

        Scene? newScene = null;
        lock (_sceneLock)
        {
            if (_pendingScene is not null)
            {
                newScene = _pendingScene;
                _pendingScene = null;
            }
        }
        if (newScene is not null)
            ApplyScene(gl, newScene);

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        uint width = (uint)Math.Max(1, Bounds.Width * scaling);
        uint height = (uint)Math.Max(1, Bounds.Height * scaling);
        gl.Viewport(0, 0, width, height);

        gl.Enable(EnableCap.DepthTest);
        gl.ClearColor(0.13f, 0.14f, 0.16f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        var eye = _target + new Vector3d(
            _distance * Math.Cos(_pitch) * Math.Cos(_yaw),
            _distance * Math.Cos(_pitch) * Math.Sin(_yaw),
            _distance * Math.Sin(_pitch));
        var view = LookAt(eye, _target, Vector3d.UnitZ);
        var proj = Perspective(Math.PI / 4, (double)width / height, 0.1, 200.0);

        Span<float> matrix = stackalloc float[16];
        gl.UseProgram(_program);
        WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(_uView, 1, false, matrix);
        WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(_uProj, 1, false, matrix);
        var lightDir = new Vector3d(-0.5, -0.7, -0.9).Normalized();
        gl.Uniform3(_uLightDir, (float)lightDir.X, (float)lightDir.Y, (float)lightDir.Z);
        gl.Uniform3(_uEyePos, (float)eye.X, (float)eye.Y, (float)eye.Z);

        for (int i = 0; i < _meshes.Count; i++)
        {
            var m = _meshes[i];
            WriteColumnMajor(m.Model, matrix);
            gl.UniformMatrix4(_uModel, 1, false, matrix);
            gl.Uniform3(_uColor, m.Color.R, m.Color.G, m.Color.B);
            gl.Uniform1(_uHighlight, i == _selected ? 1f : 0f);
            gl.BindVertexArray(m.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)m.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
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
        var viewProjection = Perspective(Math.PI / 4, width / height, 0.1, 200.0)
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
        _selected = best == _selected ? -1 : best; // clicking the selection clears it
        string? name = _selected >= 0 && _selected < _partNames.Count ? _partNames[_selected] : null;
        Report(name is not null ? $"picked '{name}'" : "picked nothing");
        if (VisualRoot is Window window)
            window.Title = name is not null ? $"{BaseTitle} — {name}" : BaseTitle;
        RequestNextFrameRendering();
    }

    /// <summary>Window title stem, restored when nothing is selected.</summary>
    public string BaseTitle { get; set; } = "EngrCAD";

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

    private unsafe GpuMesh Upload(GL gl, RenderMesh mesh, in Matrix4d model, (float, float, float) color)
    {
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
        return new GpuMesh(vao, vbo, ebo, mesh.Indices.Length, model, color);
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

    private static void WriteColumnMajor(in Matrix4d m, Span<float> dst)
    {
        dst[0] = (float)m.M11; dst[1] = (float)m.M21; dst[2] = (float)m.M31; dst[3] = (float)m.M41;
        dst[4] = (float)m.M12; dst[5] = (float)m.M22; dst[6] = (float)m.M32; dst[7] = (float)m.M42;
        dst[8] = (float)m.M13; dst[9] = (float)m.M23; dst[10] = (float)m.M33; dst[11] = (float)m.M43;
        dst[12] = (float)m.M14; dst[13] = (float)m.M24; dst[14] = (float)m.M34; dst[15] = (float)m.M44;
    }
}
