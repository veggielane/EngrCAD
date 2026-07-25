using System.Diagnostics;
using EngrCAD.Core;
using Silk.NET.OpenGL;
using GL = Silk.NET.OpenGL.GL;

namespace EngrCAD.Viewer;

// The view cube: the standard CAD orientation widget in the viewport's top-right.
// Everything cube-related lives in this file — pure pose/hit math (ViewCubeMath,
// unit-tested without GL), the camera animation (ViewCubeAnimation), the GL
// widget itself (ViewCube), and the small HoverThrottle helper the viewport's
// model-hover highlight shares. ViewportControl only calls four hooks: Step
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
/// Pure math for the view cube: region layout, pose table (matching the toolbar's
/// Front/Top/Right/Iso yaw/pitch exactly), shortest-path yaw wrapping, easing, and
/// the screen-space hit test (ortho pick ray vs the unit cube, band classification
/// into face/edge/corner). No GL, no Avalonia — unit-testable.
/// </summary>
internal static class ViewCubeMath
{
    /// <summary>Side of the square cube region, in device-independent pixels.</summary>
    public const double RegionSizeDip = 104;

    /// <summary>Gap between the region and the viewport's top/right edges (DIPs).</summary>
    public const double RegionMarginDip = 10;

    /// <summary>Orbit-camera eye distance of the mini projection (cube units).</summary>
    public const double EyeDistance = 4.0;

    /// <summary>Half-extent of the square ortho frustum; the cube's space diagonal is
    /// sqrt(3) ~ 1.732, so 1.9 keeps the cube inside the region at every angle.</summary>
    public const double OrthoHalfExtent = 1.9;

    /// <summary>Face-local coordinate (cube units, face spans [-1,1]) beyond which a
    /// hit counts toward the adjacent face: an edge band, or a corner when both
    /// coordinates exceed it.</summary>
    public const double Band = 0.55;

    /// <summary>Pitch clamp shared with the orbit camera (0.01 shy of the poles keeps
    /// LookAt's up vector non-degenerate).</summary>
    public const double PitchLimit = Math.PI / 2 - 0.01;

    /// <summary>
    /// Maps a control-space point (DIPs, y down) into the cube region's normalized
    /// device coordinates (u right, v up, both in [-1,1]); false when the point lies
    /// outside the top-right region square.
    /// </summary>
    public static bool TryMapToRegion(
        double x, double y, double controlWidth, double controlHeight, out double u, out double v)
    {
        double left = controlWidth - RegionMarginDip - RegionSizeDip;
        double top = RegionMarginDip;
        u = (x - left) / RegionSizeDip * 2 - 1;
        v = 1 - (y - top) / RegionSizeDip * 2;
        return x >= left && x <= left + RegionSizeDip
            && y >= top && y <= top + RegionSizeDip
            && controlWidth > RegionSizeDip + 2 * RegionMarginDip;
    }

    /// <summary>
    /// Screen-space hit test: casts the mini-projection's ortho pick ray for region
    /// NDC (<paramref name="u"/>, <paramref name="v"/>) at the camera orbit pose and
    /// intersects it with the unit cube [-1,1]^3. On a hit, <paramref name="direction"/>
    /// holds the view direction as integer components in {-1,0,1}: one nonzero
    /// component for a face, two for an edge band, three for a corner.
    /// </summary>
    public static bool TryHit(double yaw, double pitch, double u, double v, out Vector3d direction)
    {
        direction = default;
        var eyeDir = new Vector3d(
            Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
        var forward = -eyeDir;
        var right = forward.Cross(Vector3d.UnitZ).Normalized();
        var up = right.Cross(forward);
        var origin = eyeDir * EyeDistance + right * (u * OrthoHalfExtent) + up * (v * OrthoHalfExtent);

        Span<double> o = [origin.X, origin.Y, origin.Z];
        Span<double> d = [forward.X, forward.Y, forward.Z];
        double tEnter = double.NegativeInfinity, tExit = double.PositiveInfinity;
        int enterAxis = -1;
        double enterSign = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            // Axis-parallel ray guard (exact-zero-division protection, not a geometric
            // tolerance): a standard view direction has two exactly-zero components.
            if (Math.Abs(d[axis]) < 1e-12)
            {
                if (Math.Abs(o[axis]) > 1)
                    return false;
                continue;
            }
            double t1 = (-1 - o[axis]) / d[axis];
            double t2 = (1 - o[axis]) / d[axis];
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            if (t1 > tEnter)
            {
                tEnter = t1;
                enterAxis = axis;
                enterSign = -Math.Sign(d[axis]); // entered through the face the ray points against
            }
            tExit = Math.Min(tExit, t2);
        }
        if (enterAxis < 0 || tEnter > tExit || tExit < 0)
            return false;

        var p = origin + forward * tEnter;
        Span<double> hit = [p.X, p.Y, p.Z];
        Span<double> components = [0, 0, 0];
        components[enterAxis] = enterSign;
        for (int axis = 0; axis < 3; axis++)
        {
            if (axis == enterAxis)
                continue;
            double c = Math.Clamp(hit[axis], -1, 1);
            if (Math.Abs(c) > Band)
                components[axis] = Math.Sign(c);
        }
        direction = new Vector3d(components[0], components[1], components[2]);
        return true;
    }

    /// <summary>
    /// Orbit pose looking along <paramref name="direction"/> (from target toward eye).
    /// Face normals reproduce the toolbar poses exactly: Front (0,-1,0) is yaw -pi/2
    /// pitch 0, Right (1,0,0) is yaw 0, and the (1,-1,1) corner is the toolbar Iso
    /// (yaw -pi/4, pitch asin(1/sqrt 3)). Straight-up/down directions keep
    /// <paramref name="currentYaw"/> (yaw is unconstrained at the poles); pitch is
    /// clamped to the orbit camera's limit.
    /// </summary>
    public static (double Yaw, double Pitch) PoseFor(in Vector3d direction, double currentYaw)
    {
        var d = direction.Normalized();
        double pitch = Math.Clamp(Math.Asin(Math.Clamp(d.Z, -1, 1)), -PitchLimit, PitchLimit);
        // Exact-zero horizontal component test (semantic: the +-Z faces), squared scale.
        double yaw = d.X * d.X + d.Y * d.Y < 1e-18 ? currentYaw : Math.Atan2(d.Y, d.X);
        return (yaw, pitch);
    }

    /// <summary>
    /// The camera's own view direction (target toward eye) for an orbit pose — the
    /// inverse of <see cref="PoseFor"/> and the cube face you are looking at.
    /// </summary>
    public static Vector3d ViewDirection(double yaw, double pitch) => new(
        Math.Cos(pitch) * Math.Cos(yaw),
        Math.Cos(pitch) * Math.Sin(yaw),
        Math.Sin(pitch));

    /// <summary>
    /// The standard cube orientation nearest an arbitrary orbit pose: the one of the
    /// 26 face/edge/corner directions (components in {-1, 0, 1}, not all zero) whose
    /// direction is closest to the camera's <see cref="ViewDirection"/>. This is what
    /// commercial cubes snap to when you finish dragging on the widget — the view
    /// settles onto a documented orientation instead of an arbitrary one. Idempotent:
    /// snapping an already-snapped pose returns the same direction.
    /// </summary>
    public static Vector3d NearestStandardDirection(double yaw, double pitch)
    {
        var view = ViewDirection(yaw, pitch);
        var best = new Vector3d(1, 0, 0);
        double bestDot = double.NegativeInfinity;
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0)
                        continue;
                    var candidate = new Vector3d(x, y, z);
                    double dot = candidate.Normalized().Dot(view);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        best = candidate;
                    }
                }
            }
        }
        return best;
    }

    /// <summary>Equivalent target yaw within half a turn of <paramref name="fromYaw"/>,
    /// so the animation always takes the shortest angular path.</summary>
    public static double ShortestYawTarget(double fromYaw, double toYaw) =>
        fromYaw + Math.IEEERemainder(toYaw - fromYaw, 2 * Math.PI);

    /// <summary>Smoothstep ease (C1 at both ends), clamped to [0,1].</summary>
    public static double Ease(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    /// <summary>Human-readable name of a hit direction ("front", "front-right-top").</summary>
    public static string Label(in Vector3d direction)
    {
        var parts = new List<string>(3);
        if (direction.Y < 0)
            parts.Add("front");
        else if (direction.Y > 0)
            parts.Add("back");
        if (direction.X < 0)
            parts.Add("left");
        else if (direction.X > 0)
            parts.Add("right");
        if (direction.Z > 0)
            parts.Add("top");
        else if (direction.Z < 0)
            parts.Add("bottom");
        return string.Join("-", parts);
    }
}

/// <summary>
/// A short eased camera transition between orbit poses: yaw takes the shortest
/// angular path, pitch is clamped to the orbit limit, distance/target are untouched
/// (the caller keeps them). Evaluate is pure in elapsed time, so it is testable and
/// drivable from the render loop without timers.
/// </summary>
internal sealed class ViewCubeAnimation
{
    private readonly double _startYaw, _startPitch, _targetYaw, _targetPitch, _duration;

    public ViewCubeAnimation(
        double startYaw, double startPitch, double targetYaw, double targetPitch,
        double durationSeconds = 0.25)
    {
        _startYaw = startYaw;
        _startPitch = startPitch;
        _targetYaw = ViewCubeMath.ShortestYawTarget(startYaw, targetYaw);
        _targetPitch = Math.Clamp(targetPitch, -ViewCubeMath.PitchLimit, ViewCubeMath.PitchLimit);
        _duration = durationSeconds;
    }

    /// <summary>Pose at <paramref name="elapsedSeconds"/> since the start; Done once
    /// the target pose is reached exactly (the last evaluation returns the target).</summary>
    public (double Yaw, double Pitch, bool Done) Evaluate(double elapsedSeconds)
    {
        double t = _duration <= 0 ? 1 : elapsedSeconds / _duration;
        double s = ViewCubeMath.Ease(t);
        return (
            _startYaw + (_targetYaw - _startYaw) * s,
            _startPitch + (_targetPitch - _startPitch) * s,
            t >= 1);
    }
}

/// <summary>
/// The GL widget: owns the cube's fill/edge/label geometry (uploaded lazily on first
/// draw, GL context current), draws it through the existing line program into a small
/// ortho viewport in the top-right corner with the depth buffer cleared (the cube
/// always sits on top of the scene), and arms/steps the camera animation for clicks.
/// All faces are flat-shaded with subtly distinct tones so orientation reads at a
/// glance; labels are stroke-font line segments lifted slightly off each face (depth
/// hides the back faces' labels).
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

    /// <summary>Face table: outward normal, in-plane right/up for the label, word,
    /// and flat fill tone (top lightest, bottom darkest — a baked-in light cue).</summary>
    private static readonly (Vector3d N, Vector3d R, Vector3d U, string Word, (float R, float G, float B) Color)[]
        Faces =
        [
            ((0, -1, 0), (1, 0, 0), (0, 0, 1), "FRONT", (0.53f, 0.56f, 0.61f)),
            ((0, 1, 0), (-1, 0, 0), (0, 0, 1), "BACK", (0.42f, 0.44f, 0.49f)),
            ((1, 0, 0), (0, 1, 0), (0, 0, 1), "RIGHT", (0.48f, 0.51f, 0.56f)),
            ((-1, 0, 0), (0, -1, 0), (0, 0, 1), "LEFT", (0.39f, 0.42f, 0.46f)),
            ((0, 0, 1), (1, 0, 0), (0, 1, 0), "TOP", (0.62f, 0.65f, 0.70f)),
            ((0, 0, -1), (1, 0, 0), (0, -1, 0), "BOTTOM", (0.33f, 0.35f, 0.39f)),
        ];

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
        for (int face = 0; face < Faces.Length; face++)
        {
            var c = Faces[face].Color;
            // Hover highlight: every face contributing a component of the hovered
            // direction brightens (one face for a face hover, two for an edge, three
            // for a corner), so the click target reads before clicking.
            if (_hover is { } hover && hover.Dot(Faces[face].N) > 0.5)
                c = (c.R + (1 - c.R) * 0.35f, c.G + (1 - c.G) * 0.35f, c.B + (1 - c.B) * 0.35f);
            gl.Uniform3(line.Color, c.R, c.G, c.B);
            gl.DrawArrays(PrimitiveType.Triangles, face * 6, 6);
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        gl.BindVertexArray(_edgeVao);
        gl.Uniform3(line.Color, 0.12f, 0.13f, 0.15f);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_edgeVertexCount);

        gl.BindVertexArray(_labelVao);
        gl.Uniform3(line.Color, 0.93f, 0.94f, 0.96f);
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

    private void BuildGeometry(GL gl)
    {
        // Fills: 6 faces x 2 triangles, position-only (drawn per face for its color).
        var fills = new List<float>(6 * 6 * 3);
        foreach (var (n, r, u, _, _) in Faces)
        {
            var a = n - r - u;
            var b = n + r - u;
            var c = n + r + u;
            var d = n - r + u;
            AddVertex(fills, a);
            AddVertex(fills, b);
            AddVertex(fills, c);
            AddVertex(fills, a);
            AddVertex(fills, c);
            AddVertex(fills, d);
        }
        (_fillVao, _fillVbo) = RenderGeometry.UploadLines(gl, [.. fills]);

        // Edges: the 12 cube edges as line segments.
        var edges = new List<(Vector3d A, Vector3d B)>();
        Span<double> s = [-1, 1];
        foreach (double i in s)
        {
            foreach (double j in s)
            {
                edges.Add(((i, j, -1), (i, j, 1)));
                edges.Add(((i, -1, j), (i, 1, j)));
                edges.Add(((-1, i, j), (1, i, j)));
            }
        }
        float[] edgeVertices = RenderGeometry.SegmentVertices(edges);
        _edgeVertexCount = edges.Count * 2;
        (_edgeVao, _edgeVbo) = RenderGeometry.UploadLines(gl, edgeVertices);

        // Labels: stroke-font words per face, lifted slightly off the surface so they
        // beat the (polygon-offset-pushed) fills; back faces' labels lose the depth
        // test against the front fills, so only visible faces show text.
        var labels = new List<(Vector3d A, Vector3d B)>();
        foreach (var (n, r, u, word, _) in Faces)
            AddWord(labels, n * 1.01, r, u, word);
        float[] labelVertices = RenderGeometry.SegmentVertices(labels);
        _labelVertexCount = labels.Count * 2;
        (_labelVao, _labelVbo) = RenderGeometry.UploadLines(gl, labelVertices);
    }

    private static void AddVertex(List<float> vertices, in Vector3d p)
    {
        vertices.Add((float)p.X);
        vertices.Add((float)p.Y);
        vertices.Add((float)p.Z);
    }

    // ---- face labels (lettering lives in the shared StrokeFont) ----

    /// <summary>Lays a word out centered on a face: letters scaled to fit a 1.5-unit
    /// line (face spans 2 units) capped at 0.5-unit height, mapped into the face
    /// plane at <paramref name="center"/> via the face's right/up frame. The strokes
    /// come from the viewer-wide <see cref="StrokeFont"/> (this widget's original
    /// lettering, now shared with annotation text).</summary>
    private static void AddWord(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d center, in Vector3d right, in Vector3d up,
        string word)
    {
        double rawWidth = StrokeFont.TextWidth(word);
        double scale = Math.Min(0.5, 1.5 / rawWidth);
        var origin = center + right * (-rawWidth * scale / 2) + up * (-scale / 2);
        StrokeFont.AppendText(segments, word, origin, right, up, scale);
    }
}

/// <summary>
/// Distance-based sampling throttle for the viewport's model-hover highlight: the
/// hover raycast re-runs only when the pointer has traveled at least the threshold
/// since the last sample, so slow jitter costs nothing and fast sweeps still track.
/// Pure state machine — unit-tested without UI.
/// </summary>
internal struct HoverThrottle(double thresholdDips)
{
    private readonly double _thresholdSquared = thresholdDips * thresholdDips;
    private double _lastX, _lastY;
    private bool _hasSample;

    /// <summary>True when the position is far enough from the last accepted sample
    /// (or no sample exists); accepting records the position.</summary>
    public bool ShouldSample(double x, double y)
    {
        if (_hasSample)
        {
            double dx = x - _lastX, dy = y - _lastY;
            if (dx * dx + dy * dy < _thresholdSquared)
                return false;
        }
        _lastX = x;
        _lastY = y;
        _hasSample = true;
        return true;
    }

    /// <summary>Forgets the last sample so the next move re-picks immediately
    /// (drag ended, scene changed).</summary>
    public void Reset() => _hasSample = false;
}
