using EngrCAD.Core;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

// Construction-tree preview overlay: the line geometry of ONE selected construction
// row — a sketch drawn on its plane, or the feature edges of an intermediate
// operation's geometry (a rollback view). Self-contained like the annotation, isoline,
// and view-cube layers: pure state plus one line-program draw, so ViewportControl only
// calls Set/Draw/Release and never grows preview logic of its own.
//
// Deliberate choices:
// - LINES, never a mesh. The preview reuses the existing line program, which means no
//   shader, uniform, or render-pass changes; a rollback silhouette over the final body
//   reads better than a second opaque solid anyway.
// - Always-on-top (depth test off) and never section-clipped, following the annotation
//   precedent: a preview is an inspection aid, so it must be visible THROUGH the model
//   it is being compared against.
// - Geometry arrives already built (EngrCAD.Modeling's ConstructionPreview, produced on
//   a background task); this layer only uploads it inside the render pass, where the
//   GL context is current.

/// <summary>
/// GL-side owner of the construction preview: holds one batch of world-space line
/// segments plus the instance placement they belong to, uploads it lazily inside the
/// render pass, and draws it over the scene. All GL calls require a current context.
/// </summary>
internal sealed class PreviewLayer
{
    /// <summary>Construction cyan — deliberately NOT the selection gold, so a preview
    /// reads as "this is the step you picked", not "this part is selected".</summary>
    private static readonly (float R, float G, float B) Color = (0.35f, 0.92f, 1.0f);

    private readonly Lock _lock = new();
    private (Vector3d A, Vector3d B)[] _segments = [];
    private Matrix4d _world = Matrix4d.Identity;
    private bool _dirty;

    private bool _uploaded;
    private uint _vao, _vbo;
    private int _vertexCount;

    /// <summary>Whether a preview is currently showing.</summary>
    public bool HasPreview
    {
        get
        {
            lock (_lock)
                return _segments.Length > 0;
        }
    }

    /// <summary>
    /// Replaces the preview (null or empty clears it). <paramref name="world"/> is the
    /// instance placement the segments belong to — part-local geometry is posed by it,
    /// exactly as the mesh draws are. Thread-safe: hosts call this from the UI thread
    /// while the render thread draws.
    /// </summary>
    public void Set(IReadOnlyList<(Vector3d A, Vector3d B)>? segments, in Matrix4d world)
    {
        lock (_lock)
        {
            _segments = segments is null ? [] : [.. segments];
            _world = world;
            _dirty = true;
        }
    }

    /// <summary>Draws the preview through the shared line program (depth test off —
    /// always-on-top, like annotations; section clipping disabled). Uploads first when
    /// the batch changed since the last frame.</summary>
    public void Draw(GL gl, uint lineProgram, int uModel, int uColor, int uSectionEnabled, Span<float> matrix)
    {
        Matrix4d world;
        lock (_lock)
        {
            if (_dirty)
            {
                Upload(gl);
                _dirty = false;
            }
            world = _world;
        }
        if (_vertexCount == 0)
            return;

        gl.UseProgram(lineProgram);
        CameraMath.WriteColumnMajor(world, matrix);
        gl.UniformMatrix4(uModel, 1, false, matrix);
        gl.Uniform1(uSectionEnabled, 0f);   // an inspection aid, not model geometry
        gl.Uniform3(uColor, Color.R, Color.G, Color.B);

        gl.Disable(EnableCap.DepthTest);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_vertexCount);
        gl.BindVertexArray(0);
        gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Deletes the GPU buffers (deinit path; GL context current).</summary>
    public void Release(GL gl)
    {
        if (!_uploaded)
            return;
        gl.DeleteBuffer(_vbo);
        gl.DeleteVertexArray(_vao);
        _uploaded = false;
        _vertexCount = 0;
    }

    private void Upload(GL gl)
    {
        Release(gl);
        if (_segments.Length == 0)
            return;
        (_vao, _vbo) = RenderGeometry.UploadLines(gl, RenderGeometry.SegmentVertices(_segments));
        _vertexCount = _segments.Length * 2;
        _uploaded = true;
    }
}
