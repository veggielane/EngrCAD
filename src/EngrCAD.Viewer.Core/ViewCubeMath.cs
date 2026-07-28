using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// The pure half of the view cube — pose/hit math, the eased camera transition, and the
// cube's fill/edge/label geometry — shared by every front end that shows the widget.
// The GL widget itself (upload, draw, hover state) stays in EngrCAD.Viewer's
// ViewCube.cs; the browser client builds the same geometry through WebGL2. One pose
// table matters more than it looks: the desktop toolbar's Front/Top/Right/Iso buttons,
// the cube's clicks and the MCP server's named views must all agree about what "Front"
// means, and they can only agree by calling the same function.

/// <summary>
/// Pure math for the view cube: region layout, pose table (matching the toolbar's
/// Front/Top/Right/Iso yaw/pitch exactly), shortest-path yaw wrapping, easing, and
/// the screen-space hit test (ortho pick ray vs the unit cube, band classification
/// into face/edge/corner). No GL, no Avalonia — unit-testable, and shared by the
/// desktop widget and the browser client.
/// </summary>
public static class ViewCubeMath
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
    /// LookAt's up vector non-degenerate). Defined by <see cref="CameraMath.PitchLimit"/>
    /// rather than repeated: the cube's poses and the orbit clamp must be the same number,
    /// or snapping to Top would be undone by the very next clamp.</summary>
    public const double PitchLimit = CameraMath.PitchLimit;

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

    /// <summary>The standard view names every front end offers (toolbar buttons,
    /// <c>screenshot</c>'s named views, the remote-control <c>set_view</c> method), in
    /// discovery order. Delegates to <see cref="StandardViews.Names"/> — see
    /// <see cref="DirectionFor"/>.</summary>
    public static IReadOnlyList<string> StandardViewNames => StandardViews.Names;

    /// <summary>
    /// The view direction (target toward eye) of a standard view name, or null for an
    /// unknown name — the name table behind every front end's named views.
    ///
    /// <para>The vectors themselves live in <see cref="StandardViews"/>, one layer
    /// down in the modelling assembly, because a DRAWING SHEET needs the same table
    /// and is built with no viewer in the room: a sheet's front view and this
    /// widget's Front button must be the same direction, and the only way to
    /// guarantee that is to read one table.</para>
    /// </summary>
    public static Vector3d? DirectionFor(string view) => StandardViews.DirectionFor(view);

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
/// drivable from any render loop without timers. The default 0.25 s duration IS the
/// product's view-transition feel — front ends take it rather than re-typing it.
/// </summary>
public sealed class ViewCubeAnimation
{
    private readonly double _startYaw, _startPitch, _targetYaw, _targetPitch, _duration;

    /// <summary>Arms a transition from a start pose to a target pose.</summary>
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

/// <summary>One cube face: outward normal, the in-plane right/up frame its label is
/// laid out in, the label word, and the flat fill tone (top lightest, bottom darkest —
/// a baked-in light cue).</summary>
/// <param name="Normal">Outward face normal (unit, axis-aligned).</param>
/// <param name="Right">In-plane label right direction.</param>
/// <param name="Up">In-plane label up direction.</param>
/// <param name="Word">The face's label.</param>
/// <param name="Color">Flat fill tone.</param>
public readonly record struct ViewCubeFace(
    Vector3d Normal, Vector3d Right, Vector3d Up, string Word, (float R, float G, float B) Color);

/// <summary>
/// The view cube's geometry and palette, in one place: the face table, the fill /
/// edge / label vertex builders, and the hover-brighten rule. Both the desktop widget
/// and the browser client upload exactly these arrays, so the cube cannot look
/// different between front ends. Fills are drawn per face (six vertices each, in table
/// order) so each face gets its own colour uniform.
/// </summary>
public static class ViewCubeGeometry
{
    /// <summary>Vertices per face in <see cref="BuildFillVertices"/> (two triangles).</summary>
    public const int VerticesPerFace = 6;

    /// <summary>How far toward white a hovered face's fill moves (see
    /// <see cref="Brightened"/>).</summary>
    public const float HoverBrighten = 0.35f;

    /// <summary>Cube edge colour (near-black, over the fills).</summary>
    public static readonly (float R, float G, float B) EdgeColor = (0.12f, 0.13f, 0.15f);

    /// <summary>Label stroke colour.</summary>
    public static readonly (float R, float G, float B) LabelColor = (0.93f, 0.94f, 0.96f);

    /// <summary>Face table: FRONT/BACK/RIGHT/LEFT/TOP/BOTTOM, fills in this order.</summary>
    public static readonly IReadOnlyList<ViewCubeFace> Faces =
    [
        new((0, -1, 0), (1, 0, 0), (0, 0, 1), "FRONT", (0.53f, 0.56f, 0.61f)),
        new((0, 1, 0), (-1, 0, 0), (0, 0, 1), "BACK", (0.42f, 0.44f, 0.49f)),
        new((1, 0, 0), (0, 1, 0), (0, 0, 1), "RIGHT", (0.48f, 0.51f, 0.56f)),
        new((-1, 0, 0), (0, -1, 0), (0, 0, 1), "LEFT", (0.39f, 0.42f, 0.46f)),
        new((0, 0, 1), (1, 0, 0), (0, 1, 0), "TOP", (0.62f, 0.65f, 0.70f)),
        new((0, 0, -1), (1, 0, 0), (0, -1, 0), "BOTTOM", (0.33f, 0.35f, 0.39f)),
    ];

    /// <summary>A face fill moved <see cref="HoverBrighten"/> of the way toward white —
    /// the hover highlight. Every face contributing a component of the hovered
    /// direction brightens (one for a face hover, two for an edge, three for a
    /// corner), so the click target reads before clicking.</summary>
    public static (float R, float G, float B) Brightened((float R, float G, float B) c) =>
        (c.R + (1 - c.R) * HoverBrighten, c.G + (1 - c.G) * HoverBrighten, c.B + (1 - c.B) * HoverBrighten);

    /// <summary>Fill vertices: 6 faces x 2 triangles, position-only xyz triples, in
    /// <see cref="Faces"/> order (drawn per face for its colour).</summary>
    public static float[] BuildFillVertices()
    {
        var fills = new List<float>(Faces.Count * VerticesPerFace * 3);
        foreach (var face in Faces)
        {
            var (n, r, u) = (face.Normal, face.Right, face.Up);
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
        return [.. fills];
    }

    /// <summary>The 12 cube edges as line-program vertices (two per segment).</summary>
    public static float[] BuildEdgeVertices()
    {
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
        return RenderGeometry.SegmentVertices(edges);
    }

    /// <summary>
    /// Stroke-font label vertices for every face, lifted slightly off the surface so
    /// they beat polygon-offset-pushed fills; back faces' labels lose the depth test
    /// against the front fills, so only visible faces show text. Words are centered:
    /// letters scaled to fit a 1.5-unit line (a face spans 2 units) capped at 0.5-unit
    /// height, mapped into the face plane via the face's right/up frame.
    /// </summary>
    public static float[] BuildLabelVertices()
    {
        var labels = new List<(Vector3d A, Vector3d B)>();
        foreach (var face in Faces)
        {
            double rawWidth = StrokeFont.TextWidth(face.Word);
            double scale = Math.Min(0.5, 1.5 / rawWidth);
            var center = face.Normal * 1.01;
            var origin = center + face.Right * (-rawWidth * scale / 2) + face.Up * (-scale / 2);
            StrokeFont.AppendText(labels, face.Word, origin, face.Right, face.Up, scale);
        }
        return RenderGeometry.SegmentVertices(labels);
    }

    private static void AddVertex(List<float> vertices, in Vector3d p)
    {
        vertices.Add((float)p.X);
        vertices.Add((float)p.Y);
        vertices.Add((float)p.Z);
    }
}
