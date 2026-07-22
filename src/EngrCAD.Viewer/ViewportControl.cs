using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using EngrCAD.Core;
using EngrCAD.Mesh;
using Silk.NET.Core.Contexts;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

/// <summary>
/// OpenGL viewport rendering kernel meshes with an orbit camera.
/// Left-drag orbits, right/middle-drag pans, wheel zooms. Z is up.
/// Works on desktop GL 3.3+ and (via ANGLE on Windows) OpenGL ES 3.
/// </summary>
public sealed class ViewportControl : OpenGlControlBase
{
    private GL? _gl;
    private uint _program;
    private int _uModel, _uView, _uProj, _uColor, _uLightDir, _uEyePos;
    private readonly List<GpuMesh> _meshes = [];

    private double _yaw = 0.7;
    private double _pitch = 0.45;
    private double _distance = 11.0;
    private Vector3d _target = (0, 0, 0.2);
    private Point _lastPointer;

    private readonly record struct GpuMesh(uint Vao, uint Vbo, uint Ebo, int IndexCount, Matrix4d Model, (float R, float G, float B) Color);

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

        foreach (var (mesh, model, color) in DemoScene())
            _meshes.Add(Upload(_gl, RenderMesh.CreateFlat(mesh), model, color));
    }

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

        foreach (var m in _meshes)
        {
            WriteColumnMajor(m.Model, matrix);
            gl.UniformMatrix4(_uModel, 1, false, matrix);
            gl.Uniform3(_uColor, m.Color.R, m.Color.G, m.Color.B);
            gl.BindVertexArray(m.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)m.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        gl.BindVertexArray(0);
    }

    private static IEnumerable<(HalfEdgeMesh Mesh, Matrix4d Model, (float, float, float) Color)> DemoScene()
    {
        yield return (
            MeshPrimitives.Box(1.8, 1.8, 1.8),
            Matrix4d.CreateTranslation((-4.6, 0, 0)),
            (0.42f, 0.62f, 0.86f));
        yield return (
            MeshPrimitives.UvSphere(1.05, segments: 48, rings: 24),
            Matrix4d.CreateTranslation((-1.5, 0, 0)),
            (0.88f, 0.52f, 0.40f));
        yield return (
            MeshPrimitives.Cylinder(0.85, 1.9, segments: 48),
            Matrix4d.CreateTranslation((1.5, 0, -0.95)),
            (0.55f, 0.75f, 0.48f));
        yield return (
            LoopSubdivision.Subdivide(MeshPrimitives.Box(2.0, 2.0, 2.0).Triangulated(), 3),
            Matrix4d.CreateTranslation((4.6, 0, 0)),
            (0.72f, 0.55f, 0.83f));
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
            out vec4 fragColor;
            void main()
            {
                vec3 n = normalize(vNormal);
                float diffuse = max(dot(n, -uLightDir), 0.0);
                vec3 v = normalize(uEyePos - vWorldPos);
                vec3 h = normalize(v - uLightDir);
                float specular = pow(max(dot(n, h), 0.0), 48.0) * 0.35;
                vec3 c = uColor * (0.22 + 0.78 * diffuse) + vec3(specular);
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
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
            var eyeDir = new Vector3d(Math.Cos(_pitch) * Math.Cos(_yaw), Math.Cos(_pitch) * Math.Sin(_yaw), Math.Sin(_pitch));
            var right = eyeDir.Cross(Vector3d.UnitZ).Normalized();
            var up = right.Cross(eyeDir); // eyeDir points from target toward eye, so this is screen-up
            double scale = _distance * 0.0018;
            _target += right * (delta.X * scale) + up * (delta.Y * scale);
            RequestNextFrameRendering();
        }
        else if (zoom)
        {
            _distance = Math.Clamp(_distance * Math.Pow(1.006, delta.Y), 0.5, 120.0);
            RequestNextFrameRendering();
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            _yaw -= delta.X * 0.01;
            _pitch = Math.Clamp(_pitch + delta.Y * 0.01, -Math.PI / 2 + 0.01, Math.PI / 2 - 0.01);
            RequestNextFrameRendering();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _distance = Math.Clamp(_distance * Math.Pow(0.88, e.Delta.Y), 0.5, 120.0);
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
