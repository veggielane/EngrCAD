using EngrCAD.Core;
using EngrCAD.Modeling;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

/// <summary>
/// One construction-tree row to draw over a <see cref="EngrCad.RenderToImage"/> render —
/// the still-image form of clicking that row in the model tree, and the reason headless
/// output has the same overlays the window does. The row is identified by the
/// <see cref="Part"/> it belongs to plus the <see cref="ConstructionNode"/> itself
/// (a node carries no back-reference to its part), which also lets the build reuse the
/// part's cached solid for the root row instead of lowering it a second time.
/// </summary>
/// <param name="Part">The part the row belongs to (<c>Part.ConstructionTree()</c>).</param>
/// <param name="Node">The row: the whole part, a sub-operation, or a sketch.</param>
public readonly record struct ConstructionPreviewRequest(Part Part, ConstructionNode Node)
{
    /// <summary>
    /// Builds the row's line geometry and the world matrix to pose it by. Lowers
    /// geometry, so callers run it before touching GL (headless) or on a background task
    /// (the window). A row that cannot be previewed throws rather than rendering a
    /// silently empty overlay — a docs snippet asking for a broken preview must fail.
    /// </summary>
    internal (IReadOnlyList<(Vector3d A, Vector3d B)>? Segments, Matrix4d World) Build(
        IReadOnlyList<PartInstance> instances, MeshQuality? quality)
    {
        ArgumentNullException.ThrowIfNull(Part);
        ArgumentNullException.ThrowIfNull(Node);
        var world = Part.Transform;
        foreach (var instance in instances)
        {
            if (ReferenceEquals(instance.Part, Part))
            {
                world = instance.World;
                break;
            }
        }

        // The root row IS the part's own geometry, which PreMesh already lowered.
        var known = ReferenceEquals(Node.Target, Part.Geometry) ? Part.TryGetSolid() : null;
        var preview = ConstructionPreview.Build(Node, quality, known);
        if (preview.Error is { } error)
            throw new InvalidOperationException(error);
        return (preview.Segments, world);
    }
}

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
