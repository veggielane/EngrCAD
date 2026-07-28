using System.Diagnostics;
using EngrCAD.Core;
using Silk.NET.OpenGL;
using GL = Silk.NET.OpenGL.GL;

namespace EngrCAD.Viewer;

// The view cube: the standard CAD orientation widget in the viewport's top-right.
// The pure halves — pose/hit math (ViewCubeMath), the camera animation
// (ViewCubeAnimation) and the fill/edge/label geometry with its palette
// (ViewCubeGeometry) — live in EngrCAD.Viewer.Core so the browser client shares them;
// this file keeps the GL widget only. The HoverThrottle helper that used to live here
// moved the same way when the browser client needed the same hover feel.
// ViewportControl only calls four hooks: Step
// (animation, before the camera matrices are built), Draw (end of the render
// pass), HandleClick (pointer pre-check before scene picking), and UpdateHover
// (pointer-move pre-check for the hover highlight).
//
// Deliberate choices:
// - The mini-projection is ALWAYS orthographic regardless of the main projection
//   toggle — standard for orientation widgets, and it keeps the screen-space hit
//   test an exact ortho ray-vs-cube slab test.
// - The cube is interactive window chrome: the headless OffscreenRenderer excludes
//   it by design (docs renders and pixel tests see only the model).
// - Labels are stroke-font line segments drawn with the existing line program (no
//   text renderer, no new shaders); full words FRONT/BACK/LEFT/RIGHT/TOP/BOTTOM.

/// <summary>The uniform/program handles of the flat-color line program, passed to the
/// cube so it can reuse the existing shader (no cube-specific programs).</summary>
internal readonly record struct LineProgramHandles(
    uint Program, int Model, int View, int Proj, int Color, int SectionEnabled);

/// <summary>
/// The GL widget: owns the cube's fill/edge/label geometry (built by
/// <see cref="ViewCubeGeometry"/>, uploaded lazily on first draw, GL context current),
/// draws it through the existing line program into a small ortho viewport in the
/// top-right corner with the depth buffer cleared (the cube always sits on top of the
/// scene), and arms/steps the camera animation for clicks.
/// </summary>
internal sealed class ViewCube
{
    private uint _fillVao, _fillVbo, _edgeVao, _edgeVbo, _labelVao, _labelVbo;
    private int _edgeVertexCount, _labelVertexCount;
    private bool _initialized;
    private ViewCubeAnimation? _animation;
    private long _animationStart;
    private Vector3d? _hover;

    /// <summary>The hovered region's direction (components in {-1,0,1}; null when the
    /// pointer is not over the cube). Every face contributing a component highlights,
    /// so an edge lights two faces and a corner three.</summary>
    public Vector3d? Hover => _hover;

    /// <summary>
    /// Handles a click at a control-space position. Returns true when the position is
    /// inside the cube's screen region (the region claims the click even between the
    /// cube's silhouette and the region corners, so parts behind the widget are never
    /// picked through it). When the cube itself was hit, arms the camera animation
    /// toward the face/edge/corner view and reports its name in <paramref name="view"/>.
    /// </summary>
    public bool HandleClick(
        double x, double y, double controlWidth, double controlHeight,
        double yaw, double pitch, out string? view)
    {
        view = null;
        if (!ViewCubeMath.TryMapToRegion(x, y, controlWidth, controlHeight, out double u, out double v))
            return false;
        if (ViewCubeMath.TryHit(yaw, pitch, u, v, out var direction))
            view = AnimateTo(direction, yaw, pitch);
        return true;
    }

    /// <summary>
    /// Rotate-snap: settles the camera onto the standard orientation nearest the
    /// current pose, the way commercial cubes finish a drag on the widget. Returns the
    /// view's name. Called when a drag that STARTED on the cube ends, so free orbiting
    /// elsewhere in the viewport is untouched.
    /// </summary>
    public string SnapToNearest(double yaw, double pitch) =>
        AnimateTo(ViewCubeMath.NearestStandardDirection(yaw, pitch), yaw, pitch);

    /// <summary>Arms the eased pose animation toward a cube direction.</summary>
    private string AnimateTo(in Vector3d direction, double yaw, double pitch)
    {
        var (targetYaw, targetPitch) = ViewCubeMath.PoseFor(direction, yaw);
        _animation = new ViewCubeAnimation(yaw, pitch, targetYaw, targetPitch);
        _animationStart = Stopwatch.GetTimestamp();
        return ViewCubeMath.Label(direction);
    }

    /// <summary>Whether a control-space position lies in the cube's screen region
    /// (the press check that arms rotate-snap on drag end).</summary>
    public static bool InRegion(double x, double y, double controlWidth, double controlHeight) =>
        ViewCubeMath.TryMapToRegion(x, y, controlWidth, controlHeight, out _, out _);

    /// <summary>
    /// Updates the hover state for a pointer-move at a control-space position.
    /// Returns true when the position lies inside the cube's screen region (the
    /// caller suppresses model hover there); <paramref name="changed"/> is true when
    /// the hovered face/edge/corner actually changed, so the caller redraws only on
    /// transitions, not every move.
    /// </summary>
    public bool UpdateHover(
        double x, double y, double controlWidth, double controlHeight,
        double yaw, double pitch, out bool changed)
    {
        bool inside = ViewCubeMath.TryMapToRegion(x, y, controlWidth, controlHeight, out double u, out double v);
        Vector3d? hover = inside && ViewCubeMath.TryHit(yaw, pitch, u, v, out var direction)
            ? direction
            : null;
        changed = !Nullable.Equals(hover, _hover);
        _hover = hover;
        return inside;
    }

    /// <summary>Clears the hover highlight (drag started / pointer left).</summary>
    public bool ClearHover()
    {
        bool changed = _hover is not null;
        _hover = null;
        return changed;
    }

    /// <summary>True while a pose animation is in flight (the render loop keeps
    /// requesting frames until it lands).</summary>
    public bool Animating => _animation is not null;

    /// <summary>Advances the animation; true when it produced a pose this frame (the
    /// final call returns the exact target and clears the animation).</summary>
    public bool Step(out double yaw, out double pitch)
    {
        yaw = pitch = 0;
        if (_animation is null)
            return false;
        var (y, p, done) = _animation.Evaluate(Stopwatch.GetElapsedTime(_animationStart).TotalSeconds);
        yaw = y;
        pitch = p;
        if (done)
            _animation = null;
        return true;
    }

    /// <summary>Draws the cube (call at the end of the render pass, GL context
    /// current). Clears the depth buffer so the overlay wins against the scene, draws
    /// into the top-right sub-viewport, and restores the full viewport after.</summary>
    public void Draw(GL gl, uint fbWidth, uint fbHeight, double scaling, double yaw, double pitch,
        in LineProgramHandles line)
    {
        int sizePx = (int)Math.Round(ViewCubeMath.RegionSizeDip * scaling);
        int marginPx = (int)Math.Round(ViewCubeMath.RegionMarginDip * scaling);
        int x = (int)fbWidth - sizePx - marginPx;
        int y = (int)fbHeight - sizePx - marginPx;
        if (sizePx < 16 || x < 0 || y < 0)
            return; // viewport too small for the widget
        if (!_initialized)
        {
            BuildGeometry(gl);
            _initialized = true;
        }

        // Depth-clear makes the cube an overlay (glClear ignores the viewport rect,
        // but the scene's depth buffer is finished with — the cube is drawn last).
        gl.Clear((uint)ClearBufferMask.DepthBufferBit);
        gl.Viewport(x, y, (uint)sizePx, (uint)sizePx);

        var eye = CameraMath.Eye(yaw, pitch, ViewCubeMath.EyeDistance, Vector3d.Zero);
        var view = CameraMath.LookAt(eye, Vector3d.Zero, Vector3d.UnitZ);
        // Always orthographic (independent of the main perspective/ortho toggle) —
        // standard for orientation widgets, and it matches the hit test's ortho ray.
        var proj = CameraMath.Orthographic(ViewCubeMath.OrthoHalfExtent, 1, 0.5, 8);

        Span<float> matrix = stackalloc float[16];
        gl.UseProgram(line.Program);
        gl.Uniform1(line.SectionEnabled, 0f); // widget chrome — never section-clipped
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(line.Model, 1, false, matrix);
        CameraMath.WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(line.View, 1, false, matrix);
        CameraMath.WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(line.Proj, 1, false, matrix);

        // Fills pushed back slightly so edges and labels win the depth test cleanly
        // (same trick as the scene's feature-edge overlay).
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1f, 1f);
        gl.BindVertexArray(_fillVao);
        for (int face = 0; face < ViewCubeGeometry.Faces.Count; face++)
        {
            var c = ViewCubeGeometry.Faces[face].Color;
            // Hover highlight (the shared rule): every face contributing a component of
            // the hovered direction brightens, so the click target reads before clicking.
            if (_hover is { } hover && hover.Dot(ViewCubeGeometry.Faces[face].Normal) > 0.5)
                c = ViewCubeGeometry.Brightened(c);
            gl.Uniform3(line.Color, c.R, c.G, c.B);
            gl.DrawArrays(
                PrimitiveType.Triangles,
                face * ViewCubeGeometry.VerticesPerFace, ViewCubeGeometry.VerticesPerFace);
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        gl.BindVertexArray(_edgeVao);
        var edgeColor = ViewCubeGeometry.EdgeColor;
        gl.Uniform3(line.Color, edgeColor.R, edgeColor.G, edgeColor.B);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_edgeVertexCount);

        gl.BindVertexArray(_labelVao);
        var labelColor = ViewCubeGeometry.LabelColor;
        gl.Uniform3(line.Color, labelColor.R, labelColor.G, labelColor.B);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_labelVertexCount);

        gl.BindVertexArray(0);
        gl.Viewport(0, 0, fbWidth, fbHeight);
    }

    /// <summary>Deletes the cube's GL resources (context current; deinit path).</summary>
    public void DeleteResources(GL gl)
    {
        if (!_initialized)
            return;
        gl.DeleteBuffer(_fillVbo);
        gl.DeleteVertexArray(_fillVao);
        gl.DeleteBuffer(_edgeVbo);
        gl.DeleteVertexArray(_edgeVao);
        gl.DeleteBuffer(_labelVbo);
        gl.DeleteVertexArray(_labelVao);
        _initialized = false;
    }

    /// <summary>Uploads the shared cube geometry (fills, edges, labels) — the same
    /// arrays the browser client uploads, built by <see cref="ViewCubeGeometry"/>.</summary>
    private void BuildGeometry(GL gl)
    {
        (_fillVao, _fillVbo) = RenderUploads.UploadLines(gl, ViewCubeGeometry.BuildFillVertices());

        float[] edgeVertices = ViewCubeGeometry.BuildEdgeVertices();
        _edgeVertexCount = edgeVertices.Length / 3;
        (_edgeVao, _edgeVbo) = RenderUploads.UploadLines(gl, edgeVertices);

        float[] labelVertices = ViewCubeGeometry.BuildLabelVertices();
        _labelVertexCount = labelVertices.Length / 3;
        (_labelVao, _labelVbo) = RenderUploads.UploadLines(gl, labelVertices);
    }
}
