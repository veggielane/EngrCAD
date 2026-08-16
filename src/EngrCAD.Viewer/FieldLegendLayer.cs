using EngrCAD.Core;
using EngrCAD.Modeling;
using Silk.NET.OpenGL;
using GL = Silk.NET.OpenGL.GL;

namespace EngrCAD.Viewer;

// The GL half of the field-display colour bar. Self-contained like AnnotationLayer,
// PreviewLayer and the view cube: this file owns the buffers and the draw, and every
// decision about what a legend LOOKS like -- layout, bands, tick values, the title --
// lives in FieldLegend (EngrCAD.Viewer.Core), which the browser client builds from too.
//
// Unlike the view cube it IS drawn offscreen. A legend is documentation, the same
// argument that puts dimensions in a headless render: a colour plot without its scale
// is a picture of nothing in particular.

/// <summary>
/// Draws the field legend through the shared line program: flat-coloured bands for the
/// colour bar, then the outline, ticks and stroke-font labels. Geometry is rebuilt only
/// when the display, the viewport size or the pixel scale changes.
/// </summary>
internal sealed class FieldLegendLayer
{
    private uint _bandVao, _bandVbo, _lineVao, _lineVbo;
    private FieldLegendGeometry _geometry = FieldLegendGeometry.Empty;
    private (ResolvedFieldDisplay[] Displays, double Width, double Height, double Scale)? _key;
    private int _frameVertexCount, _labelVertexCount;

    /// <summary>
    /// Draws the legend for <paramref name="display"/> (nothing when it is null, or when
    /// the viewport is too small for the widget). Call at the end of the render pass with
    /// the context current; the widget is drawn with the depth test OFF, so it always
    /// reads over the model, and it is never section-clipped — it is chrome.
    /// </summary>
    public void Draw(
        GL gl, IReadOnlyList<ResolvedFieldDisplay> displays,
        double widthPx, double heightPx, double pixelScale,
        in LineProgramHandles line)
    {
        // The cache key compares display CONTENTS (record equality per entry), so a
        // frame with the same stack rebuilds nothing and a changed factor or set does.
        bool same = _key is { } k && k.Width == widthPx && k.Height == heightPx
            && k.Scale == pixelScale && k.Displays.Length == displays.Count;
        if (same)
        {
            var cached = _key!.Value.Displays;
            for (int i = 0; i < cached.Length && same; i++)
                same = cached[i].Equals(displays[i]);
        }
        if (!same)
        {
            _key = ([.. displays], widthPx, heightPx, pixelScale);
            _geometry = displays.Count > 0
                ? FieldLegend.Build(displays, widthPx, heightPx, pixelScale)
                : FieldLegendGeometry.Empty;
            Upload(gl);
        }
        if (!_geometry.HasContent)
            return;

        Span<float> matrix = stackalloc float[16];
        gl.UseProgram(line.Program);
        gl.Uniform1(line.SectionEnabled, 0f);   // chrome, never section-clipped
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(line.Model, 1, false, matrix);
        gl.UniformMatrix4(line.View, 1, false, matrix);
        CameraMath.WriteColumnMajor(_geometry.Projection, matrix);
        gl.UniformMatrix4(line.Proj, 1, false, matrix);

        // Depth off rather than depth-cleared: unlike the view cube the legend never
        // needs to be tested against ITSELF, so there is nothing to clear and one less
        // interaction with whatever drew before it.
        gl.Disable(EnableCap.DepthTest);
        gl.BindVertexArray(_bandVao);
        for (int b = 0; b < _geometry.BandCount; b++)
        {
            var (r, g, bl) = _geometry.BandColors[b];
            gl.Uniform3(line.Color, r, g, bl);
            gl.DrawArrays(
                PrimitiveType.Triangles, b * FieldLegend.VerticesPerBand, FieldLegend.VerticesPerBand);
        }

        gl.BindVertexArray(_lineVao);
        var frame = FieldLegend.FrameColor;
        gl.Uniform3(line.Color, frame.R, frame.G, frame.B);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_frameVertexCount);
        var label = FieldLegend.LabelColor;
        gl.Uniform3(line.Color, label.R, label.G, label.B);
        gl.DrawArrays(PrimitiveType.Lines, _frameVertexCount, (uint)_labelVertexCount);

        gl.BindVertexArray(0);
        gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Deletes the GL resources (context current; deinit path).</summary>
    public void Release(GL gl)
    {
        Delete(gl);
        _geometry = FieldLegendGeometry.Empty;
        _key = null;
    }

    private void Upload(GL gl)
    {
        Delete(gl);
        if (!_geometry.HasContent)
            return;
        (_bandVao, _bandVbo) = RenderUploads.UploadLines(gl, _geometry.BandVertices);
        // Frame and labels share one buffer drawn in two ranges (the world-axes trick):
        // two colours, one upload.
        _frameVertexCount = _geometry.FrameVertexCount;
        _labelVertexCount = _geometry.LabelVertexCount;
        var combined = new float[_geometry.FrameVertices.Length + _geometry.LabelVertices.Length];
        _geometry.FrameVertices.CopyTo(combined, 0);
        _geometry.LabelVertices.CopyTo(combined, _geometry.FrameVertices.Length);
        (_lineVao, _lineVbo) = RenderUploads.UploadLines(gl, combined);
    }

    private void Delete(GL gl)
    {
        if (_bandVao != 0)
        {
            gl.DeleteBuffer(_bandVbo);
            gl.DeleteVertexArray(_bandVao);
            _bandVao = _bandVbo = 0;
        }
        if (_lineVao != 0)
        {
            gl.DeleteBuffer(_lineVbo);
            gl.DeleteVertexArray(_lineVao);
            _lineVao = _lineVbo = 0;
        }
    }
}
