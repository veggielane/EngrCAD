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

    /// <summary>Geometry key, as passed to one of the upload methods.</summary>
    [JsonPropertyName("geometry")] public required string Geometry { get; init; }

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

    [JsonPropertyName("cull")] public bool Cull { get; init; } = true;

    /// <summary>Polygon offset (factor, units) — the fills-behind-edges trick the
    /// desktop viewer uses so a feature-edge overlay does not z-fight its own surface.</summary>
    [JsonPropertyName("polygonOffset")] public float[]? PolygonOffset { get; init; }
}

/// <summary>A whole frame: the clear colour and the ordered draw list.</summary>
public sealed class FrameDescription
{
    [JsonPropertyName("clear")] public required float[] Clear { get; init; }

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
        ReadOnlySpan<float> occlusion = default)
    {
        if (positions.Length != normals.Length)
            throw new ArgumentException(
                $"positions ({positions.Length}) and normals ({normals.Length}) must be the same length.",
                nameof(normals));

        return _module.InvokeVoidAsync(
            "uploadMesh", _id, key,
            PackVectors(positions), PackVectors(normals),
            MemoryMarshal.AsBytes(indices).ToArray(),
            occlusion.IsEmpty ? [] : MemoryMarshal.AsBytes(occlusion).ToArray());
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

    /// <summary>The canvas size in device-independent pixels — the camera's aspect
    /// ratio and pick-ray unprojection both need it, and only the browser knows it.</summary>
    public async ValueTask<(double Width, double Height)> ViewportSizeAsync()
    {
        var size = await _module.InvokeAsync<double[]>("viewportSize", _id);
        return (size[0], size[1]);
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

/// <summary>The browser has no WebGL2 context to give. Callers should show this rather
/// than leaving a blank canvas.</summary>
public sealed class WebGlUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// A tiny wrapper over Blazor's <c>ElementReference</c> so this file can be unit-tested
/// and read without a Razor compilation. The component passes its canvas reference.
/// </summary>
public readonly record struct ElementReferenceLike(object Value);
