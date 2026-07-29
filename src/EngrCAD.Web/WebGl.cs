using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using EngrCAD.Core;
using EngrCAD.Mesh;
using Microsoft.JSInterop;

namespace EngrCAD.Web;

/// <summary>
/// One draw call in a frame: which program, which uploaded geometry, and the uniforms
/// to set. Deliberately a plain description rather than commands — the JavaScript side
/// contains no policy, so everything about what a frame looks like is decided in .NET
/// and stays reviewable, testable and identical to the desktop passes.
/// </summary>
public sealed class DrawCall
{
    /// <summary>Program name, as passed to <see cref="WebGlContext.CreateProgramAsync"/>.</summary>
    [JsonPropertyName("program")] public required string Program { get; init; }

    /// <summary>Geometry key, as passed to one of the upload methods. Null draws
    /// <see cref="Count"/> attributeless vertices instead — what the background
    /// gradient's fullscreen triangle needs, since it builds its own corners from
    /// <c>gl_VertexID</c> and reads no buffer.</summary>
    [JsonPropertyName("geometry")] public string? Geometry { get; init; }

    /// <summary>First vertex, for line geometry (default 0). One buffer can serve
    /// several draws — the three world axes are consecutive pairs in one upload.</summary>
    [JsonPropertyName("first")] public int? First { get; init; }

    /// <summary>Vertex count for array draws (default: the whole uploaded buffer, or 3
    /// for an attributeless draw). Ignored by indexed mesh draws.</summary>
    [JsonPropertyName("count")] public int? Count { get; init; }

    /// <summary>Uniform values: float, bool, or a float array (2/3/4 = vector,
    /// 16 = column-major matrix). Names not present in the linked program are ignored,
    /// because a driver may optimize one out.</summary>
    [JsonPropertyName("uniforms")] public Dictionary<string, object>? Uniforms { get; init; }

    /// <summary><c>"points"</c> draws vertices as points; anything else draws indexed
    /// triangles (meshes) or lines (line geometry), decided by which upload created the
    /// key.</summary>
    [JsonPropertyName("mode")] public string? Mode { get; init; }

    [JsonPropertyName("blend")] public bool Blend { get; init; }

    [JsonPropertyName("depthWrite")] public bool DepthWrite { get; init; } = true;

    /// <summary>Depth testing (default on). The background gradient turns it off so it
    /// fills the frame regardless of what is already in the depth buffer.</summary>
    [JsonPropertyName("depthTest")] public bool DepthTest { get; init; } = true;

    [JsonPropertyName("cull")] public bool Cull { get; init; } = true;

    /// <summary>Polygon offset (factor, units) — the fills-behind-edges trick the
    /// desktop viewer uses so a feature-edge overlay does not z-fight its own surface.</summary>
    [JsonPropertyName("polygonOffset")] public float[]? PolygonOffset { get; init; }

    /// <summary>Framebuffer-pixel viewport rect <c>[x, y, w, h]</c> for this draw
    /// (default: the whole canvas). The view cube draws into its own top-right
    /// sub-viewport, exactly as the desktop widget does.</summary>
    [JsonPropertyName("viewport")] public int[]? Viewport { get; init; }

    /// <summary>Clears the depth buffer before this draw — what makes an overlay (the
    /// view cube) win against the finished scene. <c>glClear</c> ignores the viewport
    /// rect, so the flag is safe wherever the draw sits after the scene's own draws.</summary>
    [JsonPropertyName("clearDepth")] public bool ClearDepth { get; init; }
}

/// <summary>
/// An <c>int</c> uniform value. The interop marshals a plain JSON number through
/// <c>uniform1f</c>, which GL rejects on an int uniform — silently, since the interop
/// cannot see the GLSL declaration. This marker serializes as <c>{"int": n}</c> and the
/// JavaScript dispatches it through <c>uniform1i</c>; WHICH uniforms are ints stays a
/// C#-side decision (the shader sources live in <see cref="Viewer.ViewerShaders"/>),
/// keeping the no-policy-in-JS rule intact.
/// </summary>
/// <param name="Value">The integer value.</param>
public readonly record struct IntUniform([property: JsonPropertyName("int")] int Value);

/// <summary>
/// A <c>vec4[]</c> uniform-array value (the section planes). It cannot be a plain float
/// array: four packed planes are exactly 16 floats, which the interop's shape dispatch
/// would send through <c>uniformMatrix4fv</c> as a mat4. Serializes as
/// <c>{"vec4": [...]}</c> and dispatches through <c>uniform4fv</c>.
/// </summary>
/// <param name="Values">Packed xyzw components, four per array element.</param>
public readonly record struct Vec4ArrayUniform([property: JsonPropertyName("vec4")] float[] Values);

/// <summary>A whole frame: the clear colour and the ordered draw list.</summary>
public sealed class FrameDescription
{
    [JsonPropertyName("clear")] public required float[] Clear { get; init; }

    /// <summary>Uniforms applied before every draw call's own — the view and projection
    /// matrices, the light, and anything else constant for the frame. They travel once
    /// rather than once per instance, which for a scene of any size is most of the
    /// interop payload.</summary>
    [JsonPropertyName("shared")] public Dictionary<string, object>? Shared { get; init; }

    [JsonPropertyName("draws")] public required IReadOnlyList<DrawCall> Draws { get; init; }
}

/// <summary>
/// The WebGL2 context behind a canvas: programs, geometry, and one <see cref="RenderAsync"/>
/// per frame.
///
/// <para><b>Shaders are supplied by the caller, never written here or in JavaScript.</b>
/// The desktop window and the offscreen renderer already share one shader set for a
/// reason — they had duplicated ~150 lines and drifted silently — so the web client
/// compiles those same source strings. A copy in JavaScript would be that mistake a
/// third time, in a language where nothing would catch it.</para>
///
/// <para><b>Geometry crosses the boundary as bytes.</b> Blazor marshals <c>byte[]</c>
/// as a binary array; a <c>float[]</c> would go through JSON, which for a mesh of a few
/// hundred thousand floats is the difference between a copy and a stall. Packing here
/// keeps it to one copy.</para>
/// </summary>
public sealed class WebGlContext : IAsyncDisposable
{
    private readonly IJSObjectReference _module;
    private readonly int _id;
    private bool _disposed;

    private WebGlContext(IJSObjectReference module, int id)
    {
        _module = module;
        _id = id;
    }

    /// <summary>
    /// Loads the interop module and creates a WebGL2 context for <paramref name="canvas"/>.
    /// Throws <see cref="WebGlUnavailableException"/> when the browser has no WebGL2 —
    /// callers should catch it and show a message rather than a blank canvas.
    /// </summary>
    public static async Task<WebGlContext> CreateAsync(IJSRuntime js, ElementReferenceLike canvas)
    {
        ArgumentNullException.ThrowIfNull(js);
        var module = await js.InvokeAsync<IJSObjectReference>(
            "import", "./_content/EngrCAD.Web/engrcad-gl.js");
        try
        {
            int id = await module.InvokeAsync<int>("createContext", canvas.Value);
            return new WebGlContext(module, id);
        }
        catch (JSException e)
        {
            await module.DisposeAsync();
            throw new WebGlUnavailableException(e.Message, e);
        }
    }

    /// <summary>Compiles and links a named program from caller-supplied GLSL ES 3.00
    /// source. <paramref name="bindAttributes"/> pins position/normal/occlusion to slots
    /// 0/1/2, matching the desktop vertex layout.</summary>
    public ValueTask CreateProgramAsync(
        string name, string vertexSource, string fragmentSource, bool bindAttributes = true) =>
        _module.InvokeVoidAsync(
            "createProgram", _id, name, vertexSource, fragmentSource, bindAttributes);

    /// <summary>
    /// Uploads a triangle mesh under <paramref name="key"/>, replacing any previous
    /// geometry with that key. <paramref name="occlusion"/> is optional per-vertex
    /// ambient occlusion: omit it and the vertex attribute reads the constant 1.0, which
    /// IS the AO-off shading rather than a placeholder — the same property that lets the
    /// desktop viewer stream bakes in after the scene is already on screen.
    /// </summary>
    public ValueTask UploadMeshAsync(
        string key,
        ReadOnlySpan<Vector3d> positions,
        ReadOnlySpan<Vector3d> normals,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<float> occlusion = default,
        ReadOnlySpan<float> fieldColors = default,
        ReadOnlySpan<float> deformation = default)
    {
        if (positions.Length != normals.Length)
            throw new ArgumentException(
                $"positions ({positions.Length}) and normals ({normals.Length}) must be the same length.",
                nameof(normals));

        return _module.InvokeVoidAsync(
            "uploadMesh", _id, key,
            PackVectors(positions), PackVectors(normals),
            MemoryMarshal.AsBytes(indices).ToArray(),
            occlusion.IsEmpty ? [] : MemoryMarshal.AsBytes(occlusion).ToArray(),
            fieldColors.IsEmpty ? [] : MemoryMarshal.AsBytes(fieldColors).ToArray(),
            deformation.IsEmpty ? [] : MemoryMarshal.AsBytes(deformation).ToArray());
    }

    /// <summary>
    /// Uploads a <c>RenderMesh</c>'s already-float32 arrays under <paramref name="key"/>
    /// — the form <c>RenderMesh.CreateFlat</c> produces and the desktop's
    /// <c>RenderUploads.UploadMesh</c> consumes, so a display mesh crosses without being
    /// widened to doubles and narrowed again. <paramref name="fieldColors"/> (three
    /// floats per vertex) is the simulation-result colour buffer and
    /// <paramref name="deformation"/> (<c>FieldRendering.DeformationStride</c> floats per
    /// vertex, four interleaved vec3s) the displacement block, both optional under the
    /// same constant-when-absent rule as <paramref name="occlusion"/>.
    /// </summary>
    public ValueTask UploadMeshAsync(
        string key,
        ReadOnlySpan<float> positions,
        ReadOnlySpan<float> normals,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> occlusion = default,
        ReadOnlySpan<float> fieldColors = default,
        ReadOnlySpan<float> deformation = default)
    {
        if (positions.Length != normals.Length)
            throw new ArgumentException(
                $"positions ({positions.Length}) and normals ({normals.Length}) must be the same length.",
                nameof(normals));

        return _module.InvokeVoidAsync(
            "uploadMesh", _id, key,
            MemoryMarshal.AsBytes(positions).ToArray(),
            MemoryMarshal.AsBytes(normals).ToArray(),
            MemoryMarshal.AsBytes(indices).ToArray(),
            occlusion.IsEmpty ? [] : MemoryMarshal.AsBytes(occlusion).ToArray(),
            fieldColors.IsEmpty ? [] : MemoryMarshal.AsBytes(fieldColors).ToArray(),
            deformation.IsEmpty ? [] : MemoryMarshal.AsBytes(deformation).ToArray());
    }

    /// <summary>Uploads a line list (feature edges, wireframe, grid, axes) under
    /// <paramref name="key"/>: consecutive pairs of endpoints.</summary>
    public ValueTask UploadLinesAsync(string key, IReadOnlyList<(Vector3d A, Vector3d B)> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var packed = new float[segments.Count * 6];
        for (int i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            int at = i * 6;
            packed[at + 0] = (float)a.X; packed[at + 1] = (float)a.Y; packed[at + 2] = (float)a.Z;
            packed[at + 3] = (float)b.X; packed[at + 4] = (float)b.Y; packed[at + 5] = (float)b.Z;
        }
        return _module.InvokeVoidAsync(
            "uploadLines", _id, key, MemoryMarshal.AsBytes<float>(packed).ToArray());
    }

    /// <summary>Uploads pre-packed line vertices (x, y, z triples) — the form
    /// <c>RenderGeometry.BuildGridAndAxes</c> and <c>SegmentVertices</c> already produce,
    /// so scene furniture needs no repacking.</summary>
    public ValueTask UploadLineVerticesAsync(string key, ReadOnlySpan<float> vertices) =>
        _module.InvokeVoidAsync(
            "uploadLines", _id, key, MemoryMarshal.AsBytes(vertices).ToArray());

    /// <summary>Drops one uploaded geometry (a part removed from the scene).</summary>
    public ValueTask ReleaseGeometryAsync(string key) =>
        _module.InvokeVoidAsync("releaseGeometry", _id, key);

    /// <summary>Draws one frame.</summary>
    public ValueTask RenderAsync(FrameDescription frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return _module.InvokeVoidAsync("render", _id, frame);
    }

    /// <summary>
    /// Re-draws the last frame and reads it back as RGBA rows, <b>bottom row first</b>
    /// (raw <c>glReadPixels</c> order). The browser counterpart of the desktop viewer's
    /// screenshot, and the reason a headless run can PROVE it drew something: a black
    /// canvas raises no error, so "no errors in the log" is not evidence.
    /// </summary>
    public async ValueTask<CapturedPixels> CapturePixelsAsync()
    {
        var size = await _module.InvokeAsync<int[]>("drawingBufferSize", _id);
        var pixels = await _module.InvokeAsync<byte[]>("capture", _id);
        return new CapturedPixels(size[0], size[1], pixels);
    }

    /// <summary>Routes a drag's pointer events to the canvas until release, so a drag
    /// that leaves the element keeps orbiting — the browser's answer to the desktop
    /// viewer's window-level input handlers.</summary>
    public ValueTask CapturePointerAsync(ElementReferenceLike canvas, long pointerId) =>
        _module.InvokeVoidAsync("capturePointer", canvas.Value, pointerId);

    /// <summary>
    /// The canvas size in device-independent pixels, plus the device pixels per CSS pixel
    /// the drawing buffer is sized at. The camera's aspect ratio and pick-ray
    /// unprojection need the size; the pixel scale is what keeps a point sprite the same
    /// apparent size on a high-DPI display (the sprite is sized in the framebuffer, so it
    /// must follow the framebuffer's resolution — the desktop window multiplies by its
    /// DPI scaling and the offscreen pass by its supersample factor for the same reason).
    /// All three are browser facts, which is why they are asked for rather than assumed.
    /// </summary>
    public async ValueTask<(double Width, double Height, double PixelScale)> ViewportSizeAsync()
    {
        var size = await _module.InvokeAsync<double[]>("viewportSize", _id);
        return (size[0], size[1], size[2]);
    }

    /// <summary>float32 x/y/z triples as raw bytes — one pass, one allocation.
    /// <see cref="Vector3d"/> is doubles; GL wants float32, so this is the narrowing
    /// point and the only place it happens.</summary>
    private static byte[] PackVectors(ReadOnlySpan<Vector3d> values)
    {
        var bytes = new byte[values.Length * 12];
        // .AsSpan() matters: passing the array directly binds the ReadOnlySpan overload.
        var floats = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        for (int i = 0; i < values.Length; i++)
        {
            var v = values[i];
            floats[i * 3 + 0] = (float)v.X;
            floats[i * 3 + 1] = (float)v.Y;
            floats[i * 3 + 2] = (float)v.Z;
        }
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            await _module.InvokeVoidAsync("disposeContext", _id);
            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit or page is already gone; there is nothing left to release.
        }
    }
}

/// <summary>
/// A read-back frame: RGBA rows, <b>bottom row first</b>, at the drawing buffer's device
/// resolution (CSS size times the device pixel ratio, so it need not match the element).
/// </summary>
public readonly record struct CapturedPixels(int Width, int Height, byte[] Rgba)
{
    /// <summary>
    /// How many pixels are brighter than <paramref name="threshold"/> in every channel —
    /// the "did the lit model actually draw?" oracle, and the reason it is a *brightness*
    /// test rather than a difference from one colour: the viewport's background is a
    /// gradient (topping out at 46/255) and its ground grid is dim (74/255), so anything
    /// above about 90 is geometry under the directional light. Answered in .NET, because
    /// a black canvas raises no error and a quiet console proves nothing.
    /// </summary>
    public int CountBrighterThan(byte threshold)
    {
        int count = 0;
        for (int i = 0; i + 3 < Rgba.Length; i += 4)
        {
            if (Rgba[i] > threshold && Rgba[i + 1] > threshold && Rgba[i + 2] > threshold)
                count++;
        }
        return count;
    }
}

/// <summary>The browser has no WebGL2 context to give. Callers should show this rather
/// than leaving a blank canvas.</summary>
public sealed class WebGlUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// A tiny wrapper over Blazor's <c>ElementReference</c> so this file can be unit-tested
/// and read without a Razor compilation. The component passes its canvas reference.
/// </summary>
public readonly record struct ElementReferenceLike(object Value);
