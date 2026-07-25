using EngrCAD.Core;
using EngrCAD.Mesh;
using Silk.NET.OpenGL;
using GL = Silk.NET.OpenGL.GL;

namespace EngrCAD.Viewer;

// The shared render core: shader sources, camera/projection math, and scene-furniture
// geometry used by BOTH render paths — the interactive ViewportControl and the headless
// OffscreenRenderer. Before this file existed the two deliberately duplicated ~150
// lines and drifted silently (the offscreen pass gained a scene-scaled frustum the
// window never got); anything look- or framing-related belongs here so a change lands
// in both passes at once.

/// <summary>
/// Global view style for a whole render pass — the classic CAD view-style dropdown
/// (points / wireframe / shaded / shaded with edges). The global style decides how
/// parts render *by default*; a part whose <see cref="EngrCAD.Modeling.Part.DisplayMode"/>
/// is explicitly non-default (Wireframe or Translucent) overrides it for that part.
/// <c>DisplayMode.Shaded</c> IS the default, so it cannot override — parts left at the
/// default follow the global style. <see cref="ShadedWithEdges"/> is the traditional
/// default look; <see cref="Shaded"/> is the same fill without the feature-edge
/// overlay.
/// </summary>
public enum ViewStyle
{
    /// <summary>Vertex point sprites only (mesh density inspection).</summary>
    Points,

    /// <summary>Every mesh edge as a line, no fills ("mesh" view).</summary>
    Wireframe,

    /// <summary>Lit fills without the feature-edge overlay.</summary>
    Shaded,

    /// <summary>Lit fills with the feature-edge overlay (the default CAD look).</summary>
    ShadedWithEdges,
}

/// <summary>
/// Which world axis a section plane is perpendicular to (v1 restricts section planes
/// to the three world axes). The plane keeps everything at
/// <c>coordinate &lt;= offset</c> along the axis and clips what lies above.
/// </summary>
public enum SectionAxis
{
    /// <summary>Plane x = offset; clips world +X.</summary>
    X,

    /// <summary>Plane y = offset; clips world +Y.</summary>
    Y,

    /// <summary>Plane z = offset; clips world +Z (the classic horizontal section).</summary>
    Z,
}

/// <summary>Extensions for <see cref="SectionAxis"/> shared by both render passes.</summary>
internal static class SectionAxisExtensions
{
    /// <summary>The world-space unit vector of the axis (what uSectionAxis carries).</summary>
    public static Vector3d Direction(this SectionAxis axis) => axis switch
    {
        SectionAxis.X => Vector3d.UnitX,
        SectionAxis.Y => Vector3d.UnitY,
        _ => Vector3d.UnitZ,
    };
}

/// <summary>How one instance actually renders after combining the global
/// <see cref="ViewStyle"/> with the part's own <c>DisplayMode</c>.</summary>
internal enum EffectiveMode
{
    Points,
    Wireframe,
    Shaded,
    ShadedWithEdges,
    Translucent,
}

/// <summary>
/// Per-instance render-mode resolution and draw-order helpers shared by BOTH render
/// passes (window and offscreen), so precedence and translucency ordering cannot
/// drift between them.
/// </summary>
internal static class RenderModes
{
    /// <summary>
    /// The precedence rule, in one place: an explicit non-default part mode
    /// (Wireframe/Translucent) wins; parts at the default (Shaded) follow the global
    /// style.
    /// </summary>
    public static EffectiveMode Resolve(ViewStyle style, EngrCAD.Modeling.DisplayMode mode) => mode switch
    {
        EngrCAD.Modeling.DisplayMode.Wireframe => EffectiveMode.Wireframe,
        EngrCAD.Modeling.DisplayMode.Translucent => EffectiveMode.Translucent,
        _ => style switch
        {
            ViewStyle.Points => EffectiveMode.Points,
            ViewStyle.Wireframe => EffectiveMode.Wireframe,
            ViewStyle.Shaded => EffectiveMode.Shaded,
            _ => EffectiveMode.ShadedWithEdges,
        },
    };

    /// <summary>
    /// Sorts the first <paramref name="count"/> entries of <paramref name="order"/>
    /// descending by their <paramref name="depth"/> keys (farthest first — back-to-front
    /// alpha blending). Insertion sort over parallel arrays: no comparer delegate, no
    /// allocation, and the arrays can be reused frame to frame.
    /// </summary>
    public static void SortBackToFront(int[] order, double[] depth, int count)
    {
        for (int k = 1; k < count; k++)
        {
            int index = order[k];
            double key = depth[k];
            int j = k - 1;
            while (j >= 0 && depth[j] < key)
            {
                order[j + 1] = order[j];
                depth[j + 1] = depth[j];
                j--;
            }
            order[j + 1] = index;
            depth[j + 1] = key;
        }
    }
}

/// <summary>
/// GLSL sources and program compilation shared by the window and offscreen passes.
/// There is ONE shader set: the mesh shader carries every feature the viewport needs
/// (selection highlight, section plane, translucency); a pass that wants none of them
/// sets the neutral uniforms (uHighlight 0, uSectionEnabled 0, uAlpha 1).
/// <para>
/// HARD-WON LESSON: shader source strings must stay PURE ASCII. An em dash in a
/// comment once made ANGLE's translator reject the whole shader; the compile exception
/// aborted OnOpenGlInit before the other programs were built and the entire viewport
/// rendered black.
/// </para>
/// </summary>
internal static class ViewerShaders
{
    private const string EsHeader = "#version 300 es\nprecision highp float;\n";
    private const string DesktopHeader = "#version 330 core\n";

    /// <summary>Version header for the GL profile in use (ES3 via ANGLE, or desktop 3.3).</summary>
    public static string Header(bool es) => es ? EsHeader : DesktopHeader;

    /// <summary>Lit mesh vertex shader (position + normal + baked occlusion, world-space
    /// lighting). aOcclusion is attribute 2; a mesh uploaded without an occlusion buffer
    /// leaves that array disabled and reads the constant 1.0 set at pass init.</summary>
    public const string MeshVertex = """
        in vec3 aPos;
        in vec3 aNormal;
        in float aOcclusion;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        out vec3 vNormal;
        out vec3 vWorldPos;
        out float vOcclusion;
        void main()
        {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorldPos = world.xyz;
            vNormal = mat3(uModel) * aNormal;
            vOcclusion = aOcclusion;
            gl_Position = uProj * uView * world;
        }
        """;

    /// <summary>
    /// Lit mesh fragment shader: directional light + specular, selection highlight
    /// (uHighlight), axis-aligned section plane (uSectionEnabled/uSectionAxis/
    /// uSectionOffset — clips where dot(world, axis) exceeds the offset; cut
    /// interiors via gl_FrontFacing), translucency (uAlpha), and baked ambient
    /// occlusion (uAmbientOcclusion scales the per-vertex vOcclusion in; 0 = off and
    /// reproduces the pre-AO look exactly, since the factor is then exactly 1.0).
    /// </summary>
    public const string MeshFragment = """
        in vec3 vNormal;
        in vec3 vWorldPos;
        in float vOcclusion;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform vec3 uEyePos;
        uniform float uHighlight;
        uniform float uSectionEnabled;
        uniform vec3 uSectionAxis;
        uniform float uSectionOffset;
        uniform float uAlpha;
        uniform float uAmbientOcclusion;
        out vec4 fragColor;
        void main()
        {
            if (uSectionEnabled > 0.5)
            {
                if (dot(vWorldPos, uSectionAxis) > uSectionOffset)
                    discard;
                if (!gl_FrontFacing)
                {
                    // Cut cue: interiors exposed by the section plane show their
                    // backfaces as a flat, darker warm tint (no lighting), the
                    // standard CAD hint that you are looking at cut material.
                    // Baked occlusion is not applied here: it was baked for the
                    // outward surface, and cut material is a flat fill by design.
                    // NOTE: shader comments must stay pure ASCII - ANGLE's GLES
                    // translator rejects the whole shader on non-ASCII bytes.
                    vec3 cut = mix(uColor, vec3(0.78, 0.47, 0.25), 0.55) * 0.72;
                    fragColor = vec4(mix(cut, vec3(1.0, 0.85, 0.35), uHighlight * 0.4), uAlpha);
                    return;
                }
            }
            vec3 n = normalize(vNormal);
            float diffuse = max(dot(n, -uLightDir), 0.0);
            vec3 v = normalize(uEyePos - vWorldPos);
            vec3 h = normalize(v - uLightDir);
            float specular = pow(max(dot(n, h), 0.0), 48.0) * 0.35;
            vec3 base = mix(uColor, vec3(1.0, 0.85, 0.35), uHighlight * 0.55);
            // Occlusion darkens ambient and diffuse but not the specular highlight
            // (a direct-light term); uAmbientOcclusion 0 leaves the factor exactly 1.
            float ao = mix(1.0, vOcclusion, uAmbientOcclusion);
            vec3 c = base * (0.22 + 0.78 * diffuse) * ao + vec3(specular);
            fragColor = vec4(c, uAlpha);
        }
        """;

    /// <summary>Flat-color line vertex shader (grid, axes, feature edges, wireframe).</summary>
    public const string LineVertex = """
        in vec3 aPos;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        out vec3 vWorldPos;
        void main()
        {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorldPos = world.xyz;
            gl_Position = uProj * uView * world;
        }
        """;

    /// <summary>Flat-color line fragment shader; lines that belong to the model are
    /// clipped by the section plane consistently with the fills.</summary>
    public const string LineFragment = """
        in vec3 vWorldPos;
        uniform vec3 uColor;
        uniform float uSectionEnabled;
        uniform vec3 uSectionAxis;
        uniform float uSectionOffset;
        out vec4 fragColor;
        void main()
        {
            if (uSectionEnabled > 0.5 && dot(vWorldPos, uSectionAxis) > uSectionOffset)
                discard;
            fragColor = vec4(uColor, 1.0);
        }
        """;

    /// <summary>Point-sprite vertex shader for the points view style. gl_PointSize
    /// needs GL_PROGRAM_POINT_SIZE enabled on desktop GL (always on under GLES).</summary>
    public const string PointVertex = """
        in vec3 aPos;
        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform float uPointSize;
        out vec3 vWorldPos;
        void main()
        {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorldPos = world.xyz;
            gl_Position = uProj * uView * world;
            gl_PointSize = uPointSize;
        }
        """;

    /// <summary>Point-sprite fragment shader: round dots (square sprites trimmed via
    /// gl_PointCoord), section-clipped consistently with the model.</summary>
    public const string PointFragment = """
        in vec3 vWorldPos;
        uniform vec3 uColor;
        uniform float uSectionEnabled;
        uniform vec3 uSectionAxis;
        uniform float uSectionOffset;
        out vec4 fragColor;
        void main()
        {
            if (uSectionEnabled > 0.5 && dot(vWorldPos, uSectionAxis) > uSectionOffset)
                discard;
            vec2 d = gl_PointCoord - vec2(0.5);
            if (dot(d, d) > 0.25)
                discard;
            fragColor = vec4(uColor, 1.0);
        }
        """;

    /// <summary>Vertexless fullscreen triangle via gl_VertexID.</summary>
    public const string BackgroundVertex = """
        out vec2 vUv;
        void main()
        {
            vec2 corners[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
            vec2 p = corners[gl_VertexID];
            vUv = p * 0.5 + 0.5;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    /// <summary>Vertical background gradient, dark CAD theme.</summary>
    public const string BackgroundFragment = """
        in vec2 vUv;
        out vec4 fragColor;
        void main()
        {
            vec3 bottom = vec3(0.09, 0.10, 0.12);
            vec3 top = vec3(0.18, 0.20, 0.24);
            fragColor = vec4(mix(bottom, top, vUv.y), 1.0);
        }
        """;

    /// <summary>
    /// Compiles and links a program from full sources (header already prepended).
    /// <paramref name="bindAttributes"/> pins aPos/aNormal/aOcclusion to locations
    /// 0/1/2 (binding a name the shader does not declare is legal and ignored).
    /// </summary>
    public static uint LinkProgram(GL gl, string vertexSource, string fragmentSource, bool bindAttributes)
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
            gl.BindAttribLocation(program, 2, "aOcclusion");
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
}

/// <summary>
/// Camera and projection math shared by both render paths (rendering-only; kernel math
/// stays in EngrCAD.Core). Column-vector convention, Z up.
/// </summary>
internal static class CameraMath
{
    /// <summary>Eye position of the orbit camera.</summary>
    public static Vector3d Eye(double yaw, double pitch, double distance, in Vector3d target) =>
        target + new Vector3d(
            distance * Math.Cos(pitch) * Math.Cos(yaw),
            distance * Math.Cos(pitch) * Math.Sin(yaw),
            distance * Math.Sin(pitch));

    public static Matrix4d LookAt(in Vector3d eye, in Vector3d target, in Vector3d up)
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

    public static Matrix4d Perspective(double fovY, double aspect, double near, double far)
    {
        double t = 1.0 / Math.Tan(fovY / 2);
        return new Matrix4d(
            t / aspect, 0, 0, 0,
            0, t, 0, 0,
            0, 0, (far + near) / (near - far), 2 * far * near / (near - far),
            0, 0, -1, 0);
    }

    public static Matrix4d Orthographic(double halfHeight, double aspect, double near, double far) => new(
        1 / (halfHeight * aspect), 0, 0, 0,
        0, 1 / halfHeight, 0, 0,
        0, 0, -2 / (far - near), -(far + near) / (far - near),
        0, 0, 0, 1);

    /// <summary>
    /// Near/far planes scaled from the camera distance and scene size, so large scenes
    /// neither clip at a fixed far plane nor need a max-distance clamp (a hardcoded
    /// 0.1/200 frustum cropped scenes wider than ~100 units).
    /// </summary>
    public static (double Near, double Far) FrustumPlanes(double distance, in Aabb sceneBounds)
    {
        double sceneReach = distance + (sceneBounds.IsEmpty ? 10 : sceneBounds.Size.Length);
        double near = Math.Max(distance * 0.005, 0.01);
        double far = Math.Max(200.0, sceneReach * 2);
        return (near, far);
    }

    /// <summary>Auto-framing orbit distance for a scene of the given bounds.</summary>
    public static double FrameDistance(in Aabb bounds) => Math.Max(bounds.Size.Length * 1.25 + 1, 2);

    /// <summary>
    /// Zoom-out limit: generous multiple of the scene size (the frustum scales with the
    /// distance, so the cap only stops the scene shrinking to nothing).
    /// </summary>
    public static double MaxOrbitDistance(in Aabb sceneBounds) =>
        Math.Max(120.0, sceneBounds.IsEmpty ? 0 : sceneBounds.Size.Length * 6);

    /// <summary>Writes a Matrix4d as the column-major float[16] OpenGL expects.</summary>
    public static void WriteColumnMajor(in Matrix4d m, Span<float> dst)
    {
        dst[0] = (float)m.M11; dst[1] = (float)m.M21; dst[2] = (float)m.M31; dst[3] = (float)m.M41;
        dst[4] = (float)m.M12; dst[5] = (float)m.M22; dst[6] = (float)m.M32; dst[7] = (float)m.M42;
        dst[8] = (float)m.M13; dst[9] = (float)m.M23; dst[10] = (float)m.M33; dst[11] = (float)m.M43;
        dst[12] = (float)m.M14; dst[13] = (float)m.M24; dst[14] = (float)m.M34; dst[15] = (float)m.M44;
    }
}

/// <summary>
/// Scene-furniture geometry and GPU upload helpers shared by both render paths.
/// </summary>
internal static class RenderGeometry
{
    /// <summary>Adaptive 1-2-5 ground grid on z = 0 sized to the scene, plus RGB world
    /// axes (X then Y then Z, two vertices each, at the end of the axes array).</summary>
    public static (float[] Grid, float[] Axes) BuildGridAndAxes(in Aabb bounds)
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

    /// <summary>Rounds a raw step to the nearest 1/2/5 decade value.</summary>
    public static double NiceStep(double raw)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
        double residual = raw / magnitude;
        return magnitude * (residual < 1.5 ? 1 : residual < 3.5 ? 2 : residual < 7.5 ? 5 : 10);
    }

    /// <summary>Flattens line segments into the xyz-per-vertex array the line program draws.</summary>
    public static float[] SegmentVertices(IReadOnlyList<(Vector3d A, Vector3d B)> segments)
    {
        var vertices = new float[segments.Count * 6];
        for (int i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            vertices[i * 6 + 0] = (float)a.X;
            vertices[i * 6 + 1] = (float)a.Y;
            vertices[i * 6 + 2] = (float)a.Z;
            vertices[i * 6 + 3] = (float)b.X;
            vertices[i * 6 + 4] = (float)b.Y;
            vertices[i * 6 + 5] = (float)b.Z;
        }
        return vertices;
    }

    /// <summary>Uploads bare xyz line vertices into a fresh VAO/VBO pair.</summary>
    public static unsafe (uint Vao, uint Vbo) UploadLines(GL gl, float[] vertices)
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

    /// <summary>
    /// The occlusion value every mesh VAO reads when it has no baked-occlusion buffer:
    /// attribute 2's array stays disabled, so the shader sees this context-wide
    /// constant. MUST be set once per render pass before any mesh draw — the GL default
    /// for a disabled attribute is (0, 0, 0, 1), which would shade every part black the
    /// moment ambient occlusion is switched on.
    /// </summary>
    public static void SetDefaultOcclusion(GL gl) => gl.VertexAttrib1(2, 1f);

    /// <summary>Uploads a render mesh as interleaved position+normal with an index
    /// buffer, matching the mesh program's attribute layout. A non-null
    /// <paramref name="occlusion"/> (one baked value per vertex) is uploaded into its
    /// own buffer as attribute 2; otherwise that array stays disabled and the constant
    /// from <see cref="SetDefaultOcclusion"/> applies.</summary>
    public static unsafe (uint Vao, uint Vbo, uint Ebo, uint AoVbo) UploadMesh(
        GL gl, RenderMesh mesh, float[]? occlusion = null)
    {
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
        uint aoVbo = occlusion is null ? 0 : UploadOcclusion(gl, vao, occlusion);
        gl.BindVertexArray(0);
        return (vao, vbo, ebo, aoVbo);
    }

    /// <summary>
    /// Attaches (or replaces) a mesh VAO's baked-occlusion attribute buffer — one float
    /// per vertex at attribute 2. Separate from the interleaved position/normal buffer
    /// so switching ambient occlusion on at runtime only uploads the occlusion, never
    /// re-uploads or rebuilds the geometry. Returns the new buffer id; leaves the VAO
    /// bound (callers unbind).
    /// </summary>
    public static unsafe uint UploadOcclusion(GL gl, uint vao, float[] occlusion)
    {
        gl.BindVertexArray(vao);
        uint aoVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, aoVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, occlusion, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, sizeof(float), (void*)0);
        return aoVbo;
    }
}
