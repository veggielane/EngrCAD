using EngrCAD.Mesh;
using Silk.NET.OpenGL;
using GL = Silk.NET.OpenGL.GL;

namespace EngrCAD.Viewer;

// The GL half of the shared render core. Everything here takes a `GL`, which is exactly
// why it stayed behind: the UI-free half — shader sources, camera/projection math,
// section planes and their clip rule, render-mode precedence, scene-furniture geometry —
// moved to EngrCAD.Viewer.Core (RenderCore.cs there) so a browser front end can share it
// instead of copying it. The original rule still stands for both halves: before the
// shared core existed the window and offscreen passes duplicated ~150 lines and drifted
// silently, so anything look- or framing-related belongs in the shared file, never here.

/// <summary>
/// Program compilation for the desktop/ANGLE passes. The GLSL itself lives in
/// <see cref="ViewerShaders"/> (EngrCAD.Viewer.Core); only the linking needs a
/// <c>GL</c>, so only the linking is here.
/// </summary>
internal static class ViewerPrograms
{
    /// <summary>
    /// Compiles and links a program from full sources (header already prepended).
    /// <paramref name="bindAttributes"/> pins aPos/aNormal/aOcclusion/aFieldColor to
    /// locations 0/1/2/3 (binding a name the shader does not declare is legal and
    /// ignored).
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
            gl.BindAttribLocation(program, 3, "aFieldColor");
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
/// The section-plane uniforms of one program, and the single place either render pass
/// writes them — so the window and the headless pass cannot clip differently.
/// </summary>
internal readonly struct SectionUniforms(GL gl, uint program)
{
    private readonly int _enabled = gl.GetUniformLocation(program, "uSectionEnabled");
    private readonly int _count = gl.GetUniformLocation(program, "uSectionCount");
    private readonly int _planes = gl.GetUniformLocation(program, "uSectionPlanes");
    private readonly int _union = gl.GetUniformLocation(program, "uSectionUnion");

    /// <summary>Uploads the plane set and combine rule (program must be current).
    /// Pass an empty list to disable clipping for the following draws — that is how
    /// scene furniture (grid, axes) and the view cube stay uncut.</summary>
    public void Write(GL gl, IReadOnlyList<SectionPlane> planes, SectionCombine combine)
    {
        int count = Math.Min(planes.Count, ViewerShaders.MaxSectionPlanes);
        gl.Uniform1(_enabled, count > 0 ? 1f : 0f);
        gl.Uniform1(_count, count);
        gl.Uniform1(_union, combine == SectionCombine.Union ? 1f : 0f);
        if (count == 0)
            return;
        Span<float> packed = stackalloc float[ViewerShaders.MaxSectionPlanes * 4];
        for (int i = 0; i < count; i++)
        {
            var plane = planes[i];
            packed[i * 4] = (float)plane.Normal.X;
            packed[i * 4 + 1] = (float)plane.Normal.Y;
            packed[i * 4 + 2] = (float)plane.Normal.Z;
            packed[i * 4 + 3] = (float)plane.Offset;
        }
        gl.Uniform4(_planes, (uint)count, packed[..(count * 4)]);
    }

    /// <summary>Turns clipping off for the following draws without touching the plane
    /// set (the per-draw-group switch: furniture off, model on).</summary>
    public void SetEnabled(GL gl, bool enabled) => gl.Uniform1(_enabled, enabled ? 1f : 0f);
}

/// <summary>
/// GPU upload helpers shared by both render paths. The vertex arrays they consume are
/// built by <see cref="RenderGeometry"/> (EngrCAD.Viewer.Core).
/// </summary>
internal static class RenderUploads
{
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

    /// <summary>
    /// The field colour every mesh VAO reads when it has no field-colour buffer:
    /// attribute 3's array stays disabled, so the shader sees this context-wide
    /// constant. Set once per render pass alongside
    /// <see cref="SetDefaultOcclusion"/> — the shader's uFieldColor strength is 0 for
    /// such a part, so the value only has to be finite, but white keeps a mistake
    /// looking like "no field" instead of "black part".
    /// </summary>
    public static void SetDefaultFieldColor(GL gl) => gl.VertexAttrib3(
        3, FieldRendering.NeutralColor.R, FieldRendering.NeutralColor.G, FieldRendering.NeutralColor.B);

    /// <summary>Uploads a render mesh as interleaved position+normal with an index
    /// buffer, matching the mesh program's attribute layout. A non-null
    /// <paramref name="occlusion"/> (one baked value per vertex) is uploaded into its
    /// own buffer as attribute 2; otherwise that array stays disabled and the constant
    /// from <see cref="SetDefaultOcclusion"/> applies. <paramref name="fieldColors"/>
    /// (three floats per vertex) rides attribute 3 under the identical rule.</summary>
    public static unsafe (uint Vao, uint Vbo, uint Ebo, uint AoVbo, uint FieldVbo) UploadMesh(
        GL gl, RenderMesh mesh, float[]? occlusion = null, float[]? fieldColors = null)
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
        uint fieldVbo = fieldColors is null ? 0 : UploadFieldColors(gl, vao, fieldColors);
        gl.BindVertexArray(0);
        return (vao, vbo, ebo, aoVbo, fieldVbo);
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

    /// <summary>
    /// Attaches a mesh VAO's field-colour attribute buffer — three floats per vertex at
    /// attribute 3, the occlusion buffer's exact twin. Separate from the interleaved
    /// position/normal buffer so switching a result on, or changing its colour map,
    /// re-uploads only the colours. Returns the new buffer id; leaves the VAO bound
    /// (callers unbind).
    /// </summary>
    public static unsafe uint UploadFieldColors(GL gl, uint vao, float[] colors)
    {
        gl.BindVertexArray(vao);
        uint vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, colors, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        return vbo;
    }
}
