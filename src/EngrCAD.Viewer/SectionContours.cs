using EngrCAD.Core;
using EngrCAD.Modeling;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

// The GL half of the section-plane SDF isolines. The pure half — SectionContours
// (extraction, plane frames, the SDF route, the three family colours) and
// SectionContourGeometry — lives in EngrCAD.Viewer.Core so the browser client shares
// it; ViewportControl only calls Invalidate/Draw/Release here.

/// <summary>
/// GL-side owner of the section-isoline overlay: caches SDF routes and the built
/// geometry, rebuilds only when the section plane moves or the scene/visibility
/// changes (never per frame), and draws three colored line batches through the shared
/// line program. All methods must be called with the GL context current (render pass).
/// </summary>
internal sealed class SectionContourRenderer
{
    // Parts whose implicit lowering failed and has already been reported. The failure
    // itself is cached on the Part now, so without this set every rebuild would repeat
    // the same status line.
    private readonly HashSet<Part> _reportedFailures = [];
    private readonly List<SectionContourGeometry> _geometries = [];
    private readonly List<(uint ZeroVao, uint ZeroVbo, uint PositiveVao, uint PositiveVbo,
        uint NegativeVao, uint NegativeVbo)> _buffers = [];
    private bool _dirty = true;
    private readonly List<SectionPlane> _builtPlanes = [];
    // Scratch for the per-plane sibling clip set, reused frame to frame (the render
    // paths must not allocate per frame).
    private readonly List<SectionPlane> _siblings = [];
    private bool[] _builtVisible = [];
    private int _builtVisibleCount;
    private int _reportedParts = -1;

    /// <summary>Call when the instance list changes (scene swap/live reload): forces a
    /// rebuild. The SDF lowerings themselves are cached on the Parts and deliberately
    /// NOT dropped — a reload brings fresh parts anyway, and an unchanged part keeps its
    /// (possibly very expensive) field.</summary>
    public void Invalidate()
    {
        _dirty = true;
        _reportedFailures.Clear();
    }

    /// <summary>
    /// Draws the isolines for every active section plane, rebuilding geometry and GPU
    /// buffers first when stale. Each plane's contours are drawn clipped by its SIBLING
    /// planes (<see cref="SectionClip.Siblings"/> — that method documents and owns the
    /// rule), so a quarter cut shows each cut face's isolines only where that face is
    /// actually exposed instead of across the plane's full extent. The plane comparisons
    /// for staleness are deliberate exact equality — change detection, not geometry.
    /// </summary>
    public void Draw(
        GL gl, IReadOnlyList<PartInstance> instances, IReadOnlyList<bool> visible,
        IReadOnlyList<SectionPlane> planes, SectionCombine combine,
        uint lineProgram, int uModel, int uColor, in SectionUniforms section,
        Span<float> matrix, Action<string> report)
    {
        if (NeedsRebuild(planes, visible))
        {
            Release(gl);
            _geometries.Clear();
            int most = 0;
            double spacing = 0;
            foreach (var plane in planes)
            {
                var geometry = SectionContours.Build(
                    instances, visible,
                    SectionContours.PlaneFrame(plane.Normal.Normalized(), plane.Offset),
                    (part, message) =>
                    {
                        if (_reportedFailures.Add(part))
                            report(message);
                    });
                _geometries.Add(geometry);
                if (geometry.PartCount > most)
                {
                    most = geometry.PartCount;
                    spacing = geometry.Spacing;
                }
            }
            _dirty = false;
            _builtPlanes.Clear();
            _builtPlanes.AddRange(planes);
            SnapshotVisibility(visible);
            Upload(gl);
            if (most != _reportedParts)
            {
                _reportedParts = most;
                if (most > 0)
                    report($"section isolines: {most} part(s), spacing {spacing:G3}");
            }
        }

        gl.UseProgram(lineProgram);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uModel, 1, false, matrix);

        for (int i = 0; i < _geometries.Count; i++)
        {
            if (_geometries[i].PartCount == 0)
                continue;

            SectionClip.Siblings(planes, i, combine, _siblings);
            section.Write(gl, _siblings, SectionCombine.Union);

            // d = 0 bright gold (the exact cross-section), positive levels cool,
            // negative (inside material) warm — draw signed families first so the
            // zero contour wins any overdraw. The colours are the shared
            // SectionContours palette, never re-typed here.
            var buffers = _buffers[i];
            var geometry = _geometries[i];
            DrawBatch(gl, buffers.PositiveVao, geometry.PositiveVertices.Length / 3, uColor,
                SectionContours.PositiveColor);
            DrawBatch(gl, buffers.NegativeVao, geometry.NegativeVertices.Length / 3, uColor,
                SectionContours.NegativeColor);
            DrawBatch(gl, buffers.ZeroVao, geometry.ZeroVertices.Length / 3, uColor,
                SectionContours.ZeroColor);
        }
    }

    /// <summary>Deletes the GPU buffers (viewport deinit; GL context current).</summary>
    public void Release(GL gl)
    {
        foreach (var b in _buffers)
        {
            gl.DeleteBuffer(b.ZeroVbo);
            gl.DeleteVertexArray(b.ZeroVao);
            gl.DeleteBuffer(b.PositiveVbo);
            gl.DeleteVertexArray(b.PositiveVao);
            gl.DeleteBuffer(b.NegativeVbo);
            gl.DeleteVertexArray(b.NegativeVao);
        }
        _buffers.Clear();
    }

    private bool NeedsRebuild(IReadOnlyList<SectionPlane> planes, IReadOnlyList<bool> visible)
    {
        if (_dirty || planes.Count != _builtPlanes.Count || visible.Count != _builtVisibleCount)
            return true;
        for (int i = 0; i < planes.Count; i++)
        {
            if (planes[i] != _builtPlanes[i])
                return true;
        }
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i] != _builtVisible[i])
                return true;
        }
        return false;
    }

    private void SnapshotVisibility(IReadOnlyList<bool> visible)
    {
        if (_builtVisible.Length < visible.Count)
            _builtVisible = new bool[visible.Count];
        for (int i = 0; i < visible.Count; i++)
            _builtVisible[i] = visible[i];
        _builtVisibleCount = visible.Count;
    }

    private void Upload(GL gl)
    {
        Release(gl);
        foreach (var geometry in _geometries)
        {
            if (geometry.PartCount == 0)
            {
                _buffers.Add(default);
                continue;
            }
            var (zeroVao, zeroVbo) = RenderUploads.UploadLines(gl, geometry.ZeroVertices);
            var (positiveVao, positiveVbo) = RenderUploads.UploadLines(gl, geometry.PositiveVertices);
            var (negativeVao, negativeVbo) = RenderUploads.UploadLines(gl, geometry.NegativeVertices);
            _buffers.Add((zeroVao, zeroVbo, positiveVao, positiveVbo, negativeVao, negativeVbo));
        }
    }

    private static void DrawBatch(
        GL gl, uint vao, int vertexCount, int uColor, (float R, float G, float B) color)
    {
        if (vertexCount == 0)
            return;
        gl.Uniform3(uColor, color.R, color.G, color.B);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)vertexCount);
    }
}
