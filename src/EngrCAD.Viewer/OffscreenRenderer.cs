using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using GL = Silk.NET.OpenGL.GL;

namespace EngrCAD.Viewer;

/// <summary>
/// Headless rendering: draws parts into an offscreen ANGLE pbuffer (no window, no
/// Avalonia platform, works from tests and CI) and returns raw pixels or writes a
/// PNG. The look matches <see cref="ViewportControl"/> — background gradient, ground
/// grid + axes, directional light with specular, part colors, feature-edge overlay.
///
/// NOTE: the shader sources and the camera/projection math below are deliberate
/// duplicates of the ones in <see cref="ViewportControl"/> (which is under concurrent
/// change on another branch); keep the two in sync when the viewer look evolves.
/// Shader source strings must stay PURE ASCII — ANGLE rejects the whole shader on
/// any non-ASCII byte.
/// </summary>
public static class OffscreenRenderer
{
    private static readonly Lazy<string?> Availability = new(() =>
    {
        try
        {
            var context = EglContext.TryCreate(4, 4, out var error);
            context?.Dispose();
            return error;
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    });

    /// <summary>Whether an offscreen GL context can be created on this machine.</summary>
    public static bool IsAvailable => Availability.Value is null;

    /// <summary>Why <see cref="IsAvailable"/> is false (null when it is true).</summary>
    public static string? UnavailableReason => Availability.Value;

    /// <summary>
    /// Renders <paramref name="parts"/> and returns RGBA8 pixels, top row first
    /// (width * height * 4 bytes). Parts should be pre-meshed (<c>Scene.PreMesh</c>).
    /// A null <paramref name="camera"/> auto-frames an iso-style view exactly like the
    /// viewer's first visit (yaw 0.7, pitch 0.45, distance from the parts' bounds).
    /// <paramref name="furniture"/> controls the ground grid and axes.
    /// Throws <see cref="InvalidOperationException"/> when no GL context is available;
    /// check <see cref="IsAvailable"/> first for a graceful skip.
    /// </summary>
    public static byte[] Render(
        IReadOnlyList<Part> parts, int width, int height, CameraState? camera = null, bool furniture = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        // Render at 2x and box-downsample: deterministic anti-aliasing that works on
        // every backend (MSAA pbuffers are unreliable under ANGLE/WARP). The projection
        // uses the aspect ratio only, so the camera framing is identical.
        const int supersample = 2;
        using var egl = EglContext.TryCreate(width * supersample, height * supersample, out var error)
            ?? throw new InvalidOperationException($"Offscreen rendering is not available: {error}");
        using var gl = GL.GetApi(new LamdaNativeContext(egl.GetFunction));
        var oversized = Draw(gl, parts, width * supersample, height * supersample, camera, furniture);
        return Downsample(oversized, width, height, supersample);
    }

    /// <summary>Box-filter downsample of RGBA8 pixels by an integer factor.</summary>
    private static byte[] Downsample(byte[] source, int width, int height, int factor)
    {
        if (factor == 1)
            return source;
        int sourceWidth = width * factor;
        var result = new byte[width * height * 4];
        int samples = factor * factor;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < factor; sy++)
                {
                    int row = ((y * factor + sy) * sourceWidth + x * factor) * 4;
                    for (int sx = 0; sx < factor; sx++)
                    {
                        r += source[row];
                        g += source[row + 1];
                        b += source[row + 2];
                        a += source[row + 3];
                        row += 4;
                    }
                }
                int destination = (y * width + x) * 4;
                result[destination] = (byte)(r / samples);
                result[destination + 1] = (byte)(g / samples);
                result[destination + 2] = (byte)(b / samples);
                result[destination + 3] = (byte)(a / samples);
            }
        }
        return result;
    }

    /// <summary>Renders <paramref name="parts"/> to a PNG file. See <see cref="Render"/>.</summary>
    public static void RenderToImage(
        IReadOnlyList<Part> parts, string path, int width = 1280, int height = 800,
        CameraState? camera = null, bool furniture = true)
    {
        var pixels = Render(parts, width, height, camera, furniture);
        PngWriter.Write(path, pixels, width, height);
    }

    // ---- the render pass (mirrors ViewportControl.OnOpenGlRender) ----

    private static unsafe byte[] Draw(
        GL gl, IReadOnlyList<Part> parts, int width, int height, CameraState? camera, bool furniture)
    {
        uint meshProgram = LinkProgram(gl, MeshVertexSource, MeshFragmentSource, bindAttributes: true);
        uint lineProgram = LinkProgram(gl, LineVertexSource, LineFragmentSource, bindAttributes: true);
        uint bgProgram = LinkProgram(gl, BackgroundVertexSource, BackgroundFragmentSource, bindAttributes: false);

        var bounds = Aabb.Empty;
        foreach (var part in parts)
            bounds = bounds.Union(part.Bounds());
        var cam = camera ?? DefaultCamera(bounds);

        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(0.11f, 0.12f, 0.14f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // Background gradient: vertexless fullscreen triangle, no depth.
        uint bgVao = gl.GenVertexArray();
        gl.Disable(EnableCap.DepthTest);
        gl.UseProgram(bgProgram);
        gl.BindVertexArray(bgVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.Enable(EnableCap.DepthTest);

        var eye = cam.Target + new Vector3d(
            cam.Distance * Math.Cos(cam.Pitch) * Math.Cos(cam.Yaw),
            cam.Distance * Math.Cos(cam.Pitch) * Math.Sin(cam.Yaw),
            cam.Distance * Math.Sin(cam.Pitch));
        var view = LookAt(eye, cam.Target, Vector3d.UnitZ);
        // Frustum scaled from the camera and scene so large scenes neither clip at a
        // fixed far plane nor need a distance clamp (which cropped scenes wider than
        // ~100 units when the far plane was a hardcoded 200).
        double sceneReach = cam.Distance + (bounds.IsEmpty ? 10 : bounds.Size.Length);
        double nearPlane = Math.Max(cam.Distance * 0.005, 0.01);
        double farPlane = Math.Max(200.0, sceneReach * 2);
        var proj = Perspective(Math.PI / 4, (double)width / height, nearPlane, farPlane);

        Span<float> matrix = stackalloc float[16];

        gl.UseProgram(lineProgram);
        int uLineModel = gl.GetUniformLocation(lineProgram, "uModel");
        int uLineView = gl.GetUniformLocation(lineProgram, "uView");
        int uLineProj = gl.GetUniformLocation(lineProgram, "uProj");
        int uLineColor = gl.GetUniformLocation(lineProgram, "uColor");
        WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(uLineView, 1, false, matrix);
        WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(uLineProj, 1, false, matrix);
        WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uLineModel, 1, false, matrix);

        if (furniture)
        {
            var (gridVertices, axesVertices) = BuildGridAndAxes(bounds);
            var (gridVao, _) = UploadLines(gl, gridVertices);
            var (axesVao, _) = UploadLines(gl, axesVertices);

            gl.Uniform3(uLineColor, 0.24f, 0.26f, 0.29f);
            gl.BindVertexArray(gridVao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(gridVertices.Length / 3));

            gl.BindVertexArray(axesVao);
            gl.Uniform3(uLineColor, 0.75f, 0.30f, 0.30f);
            gl.DrawArrays(PrimitiveType.Lines, 0, 2);           // +X
            gl.Uniform3(uLineColor, 0.33f, 0.66f, 0.33f);
            gl.DrawArrays(PrimitiveType.Lines, 2, 2);           // +Y
            gl.Uniform3(uLineColor, 0.35f, 0.48f, 0.85f);
            gl.DrawArrays(PrimitiveType.Lines, 4, 2);           // +Z
        }

        // Shaded fills, pushed back slightly so the edge overlay wins the depth test.
        gl.UseProgram(meshProgram);
        int uModel = gl.GetUniformLocation(meshProgram, "uModel");
        int uView = gl.GetUniformLocation(meshProgram, "uView");
        int uProj = gl.GetUniformLocation(meshProgram, "uProj");
        int uColor = gl.GetUniformLocation(meshProgram, "uColor");
        int uLightDir = gl.GetUniformLocation(meshProgram, "uLightDir");
        int uEyePos = gl.GetUniformLocation(meshProgram, "uEyePos");
        WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(uView, 1, false, matrix);
        WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(uProj, 1, false, matrix);
        var lightDir = new Vector3d(-0.5, -0.7, -0.9).Normalized();
        gl.Uniform3(uLightDir, (float)lightDir.X, (float)lightDir.Y, (float)lightDir.Z);
        gl.Uniform3(uEyePos, (float)eye.X, (float)eye.Y, (float)eye.Z);

        var fillMeshes = new List<(uint Vao, int IndexCount, Matrix4d Model, PartColor Color)>();
        var edgeMeshes = new List<(uint Vao, int VertexCount, Matrix4d Model)>();
        foreach (var part in parts)
        {
            var mesh = part.GetMesh();
            var render = RenderMesh.CreateFlat(mesh);
            fillMeshes.Add((UploadMesh(gl, render), render.Indices.Length, part.Transform, part.Color ?? Palette.Steel));

            var featureEdges = MeshFeatureEdges.Extract(mesh);
            if (featureEdges.Count > 0)
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
                var (edgeVao, _) = UploadLines(gl, edgeVertices);
                edgeMeshes.Add((edgeVao, featureEdges.Count * 2, part.Transform));
            }
        }

        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1f, 1f);
        foreach (var (vao, indexCount, model, color) in fillMeshes)
        {
            WriteColumnMajor(model, matrix);
            gl.UniformMatrix4(uModel, 1, false, matrix);
            gl.Uniform3(uColor, color.R, color.G, color.B);
            gl.BindVertexArray(vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)indexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        // Feature-edge overlay: the shaded-with-edges CAD look.
        gl.UseProgram(lineProgram);
        gl.Uniform3(uLineColor, 0.09f, 0.10f, 0.11f);
        foreach (var (vao, vertexCount, model) in edgeMeshes)
        {
            WriteColumnMajor(model, matrix);
            gl.UniformMatrix4(uLineModel, 1, false, matrix);
            gl.BindVertexArray(vao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)vertexCount);
        }
        gl.BindVertexArray(0);
        gl.Finish();

        // Read back and flip: glReadPixels returns the bottom row first.
        var bottomUp = new byte[width * height * 4];
        gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte,
            new Span<byte>(bottomUp));
        var topDown = new byte[bottomUp.Length];
        int stride = width * 4;
        for (int row = 0; row < height; row++)
            System.Buffer.BlockCopy(bottomUp, (height - 1 - row) * stride, topDown, row * stride, stride);
        return topDown;
        // GL resources are not individually deleted: the whole context (and everything
        // in it) is destroyed by the EglContext dispose in Render.
    }

    /// <summary>The viewer's first-visit framing: default yaw/pitch, distance from bounds.</summary>
    private static CameraState DefaultCamera(in Aabb bounds) => bounds.IsEmpty
        ? new CameraState(0.7, 0.45, 15.0, (0, 0, 0))
        : new CameraState(0.7, 0.45, Math.Max(bounds.Size.Length * 1.25 + 1, 2), bounds.Center);

    // ---- geometry upload ----

    private static unsafe uint UploadMesh(GL gl, RenderMesh mesh)
    {
        // Interleave position + normal (same layout as ViewportControl.Upload).
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
        return vao;
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

    /// <summary>Adaptive 1-2-5 ground grid on z = 0 plus RGB axes (mirrors ViewportControl.RebuildGrid).</summary>
    private static (float[] Grid, float[] Axes) BuildGridAndAxes(in Aabb bounds)
    {
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

        float a = extent * 0.6f;
        float[] axes =
        [
            0, 0, 0, a, 0, 0,   // +X
            0, 0, 0, 0, a, 0,   // +Y
            0, 0, 0, 0, 0, a,   // +Z
        ];
        return ([.. lines], axes);
    }

    private static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
        double residual = raw / magnitude;
        return magnitude * (residual < 1.5 ? 1 : residual < 3.5 ? 2 : residual < 7.5 ? 5 : 10);
    }

    // ---- shaders (ES3; duplicated from ViewportControl minus highlight/section) ----

    private const string Header = "#version 300 es\nprecision highp float;\n";

    private const string MeshVertexSource = Header + """
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

    private const string MeshFragmentSource = Header + """
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

    private const string LineVertexSource = Header + """
        in vec3 aPos;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        void main()
        {
            gl_Position = uProj * uView * uModel * vec4(aPos, 1.0);
        }
        """;

    private const string LineFragmentSource = Header + """
        uniform vec3 uColor;
        out vec4 fragColor;
        void main()
        {
            fragColor = vec4(uColor, 1.0);
        }
        """;

    private const string BackgroundVertexSource = Header + """
        out vec2 vUv;
        void main()
        {
            vec2 corners[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
            vec2 p = corners[gl_VertexID];
            vUv = p * 0.5 + 0.5;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    private const string BackgroundFragmentSource = Header + """
        in vec2 vUv;
        out vec4 fragColor;
        void main()
        {
            vec3 bottom = vec3(0.09, 0.10, 0.12);
            vec3 top = vec3(0.18, 0.20, 0.24);
            fragColor = vec4(mix(bottom, top, vUv.y), 1.0);
        }
        """;

    private static uint LinkProgram(GL gl, string vertexSource, string fragmentSource, bool bindAttributes)
    {
        uint vs = CompileShader(gl, ShaderType.VertexShader, vertexSource);
        uint fs = CompileShader(gl, ShaderType.FragmentShader, fragmentSource);
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        if (bindAttributes)
        {
            gl.BindAttribLocation(program, 0, "aPos");
            gl.BindAttribLocation(program, 1, "aNormal");
        }
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

    // ---- camera math (duplicated from ViewportControl; rendering-only) ----

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
