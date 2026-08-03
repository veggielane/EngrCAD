using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// The pure half of 3D annotation (PMI) rendering: dimension lines with extension lines
// and arrowheads, leader notes, datum boxes, and billboarded screen-constant text from
// the shared StrokeFont — all as plain line segments for the one line program. No GL
// anywhere in this file, which is why it lives in EngrCAD.Viewer.Core: the desktop
// AnnotationLayer (EngrCAD.Viewer) and the browser client both build their overlay
// through these functions, so a dimension cannot look different between front ends.

/// <summary>One annotation to draw: the part-local resolved form plus the instance's
/// world transform (assembly instances pose their part's annotations).</summary>
/// <param name="Annotation">The resolved part-local annotation.</param>
/// <param name="World">The instance's world transform.</param>
public readonly record struct AnnotationItem(ResolvedAnnotation Annotation, Matrix4d World);

/// <summary>
/// The camera data annotation billboarding needs, derived once per build from the
/// orbit pose: eye/basis vectors, projection kind, and the pixel-to-world conversion.
/// A record struct so value equality is a layer's rebuild key.
/// </summary>
/// <param name="Eye">Eye position.</param>
/// <param name="Forward">Unit view direction.</param>
/// <param name="Right">Unit screen-right.</param>
/// <param name="Up">Unit screen-up.</param>
/// <param name="Orthographic">Whether the projection is orthographic.</param>
/// <param name="OrthoHalfHeight">Half the vertical world extent of an ortho view.</param>
/// <param name="ViewportHeightPx">Viewport height in framebuffer pixels.</param>
/// <param name="PixelScale">Style pixels to framebuffer pixels.</param>
public readonly record struct AnnotationCamera(
    Vector3d Eye, Vector3d Forward, Vector3d Right, Vector3d Up,
    bool Orthographic, double OrthoHalfHeight, double ViewportHeightPx, double PixelScale)
{
    /// <summary>Vertical field of view of every render path's perspective projection.</summary>
    public const double FovY = Math.PI / 4;

    /// <summary>Derives the billboarding camera from an orbit pose, mirroring
    /// CameraMath.Eye/LookAt exactly (same basis every render path uses).
    /// <paramref name="pixelScale"/> converts style pixels to framebuffer pixels
    /// (the window's render scaling, the offscreen supersample factor, or the
    /// browser's device pixel ratio).</summary>
    public static AnnotationCamera From(
        in CameraState camera, bool orthographic, double viewportHeightPx, double pixelScale)
    {
        var eye = CameraMath.Eye(camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        var forward = (camera.Target - eye).Normalized();
        var right = forward.Cross(Vector3d.UnitZ).Normalized();   // pitch is clamped shy of the poles
        var up = right.Cross(forward);
        return new AnnotationCamera(eye, forward, right, up,
            orthographic, camera.Distance * Math.Tan(Math.PI / 8), viewportHeightPx, pixelScale);
    }

    /// <summary>World length of one framebuffer pixel at a world point (perspective:
    /// scales with view depth; orthographic: constant).</summary>
    public double WorldPerPixel(in Vector3d at)
    {
        if (Orthographic)
            return 2 * OrthoHalfHeight / ViewportHeightPx;
        double depth = Math.Max((at - Eye).Dot(Forward), 1e-6);
        return 2 * depth * Math.Tan(FovY / 2) / ViewportHeightPx;
    }

    /// <summary>World length of <paramref name="px"/> style pixels at a world point.</summary>
    public double PxToWorld(double px, in Vector3d at) => px * PixelScale * WorldPerPixel(at);

    /// <summary>
    /// The point moved toward the eye until its view depth has fallen by
    /// <paramref name="px"/> style pixels of world size — the depth bias
    /// <see cref="AnnotationDepth.Occluded"/> applies (see
    /// <see cref="AnnotationGeometry.OccludedDepthBiasPx"/> for why it exists).
    /// <para><b>It moves the point ALONG ITS OWN EYE RAY, which is what makes it a pure
    /// depth change.</b> Translating along the view direction instead is the obvious
    /// form and is wrong under perspective: it slides the point off its ray, so the
    /// projected position shifts by a fraction of a pixel — measured, enough to
    /// redistribute an anti-aliased 1-pixel line's coverage and put 134 changed pixels
    /// into a render whose overlay has nothing in front of it at all. Scaling about the
    /// eye leaves the screen position exact and the depth reduced by exactly the bias,
    /// so the only thing the mode can change is colour.</para>
    /// <para>The scale factor is a CONSTANT for the whole overlay, because a perspective
    /// pixel's world size is itself proportional to depth — the depth ratio cancels.
    /// Under an orthographic projection the rays are parallel, so there the plain
    /// translation IS the ray-preserving move.</para>
    /// </summary>
    public Vector3d PulledTowardEye(in Vector3d at, double px)
    {
        if (Orthographic)
            return at - Forward * PxToWorld(px, at);
        double shrink = px * PixelScale * 2 * Math.Tan(FovY / 2) / ViewportHeightPx;
        return Eye + (at - Eye) * (1 - shrink);
    }
}

/// <summary>
/// Pure geometry for the annotation overlay (no GL — unit-testable): builds the line
/// segments of dimension lines, extension lines, arrowheads, leaders, datum boxes,
/// and stroke-font text, billboarded to the camera and sized in screen pixels.
/// <para><b>Visible and hidden are never decided here.</b> Under
/// <see cref="AnnotationDepth.Occluded"/> the SAME buffer is drawn twice by the front
/// end, once accepting the fragments the model is in front of and once accepting the
/// rest — the depth buffer already holds the scene, so occlusion costs no second build,
/// no depth pre-pass and no CPU classification that three front ends could disagree
/// about. What this file does own is the one split the depth buffer cannot make: the
/// dimension's VALUE goes into a separate list from its POINTER (see
/// <see cref="Build"/>'s <c>text</c> parameter).</para>
/// </summary>
public static class AnnotationGeometry
{
    /// <summary>The overlay's line colour, shared by every front end (a second
    /// definition of what a dimension looks like would drift).</summary>
    public static readonly (float R, float G, float B) Color = (0.92f, 0.93f, 0.97f);

    /// <summary>
    /// The colour a stretch with material in front of it is drawn in under
    /// <see cref="AnnotationDepth.Occluded"/> — <see cref="Color"/> darkened, so a
    /// dimension behind the part recedes rather than disappearing.
    /// <para><b>Darker rather than lighter, and that is not a taste call.</b> A hidden
    /// fragment is by definition drawn over the occluder, never over empty space, so it
    /// only ever has to read against MATERIAL — and material here is a lit fill from the
    /// mid-tone part palette, always brighter than the background gradient. Darkening
    /// therefore gains contrast in every case the mode can produce, where lightening
    /// would lose it. Measured against the docs plate's top face at (0.51, 0.59, 0.69):
    /// a first attempt at (0.44, 0.46, 0.52) left a channel-sum contrast of 93 and the
    /// dimension line read as a smudge; this reads 231.</para>
    /// <para><b>Dimmed rather than dashed.</b> A dashed hidden line is the drafting
    /// convention this repo's own sheet output follows (<c>LineClass.Hidden</c>), and it
    /// works there because a sheet's hidden lines are model EDGES. It also has no
    /// orientation-free screen-space form: a stipple keyed on <c>gl_FragCoord</c> is
    /// constant along some screen direction, so a line parallel to it comes out solid or
    /// vanishes entirely — a real dash needs an along-the-line coordinate, which means a
    /// per-vertex attribute reaching all three front ends. Dimming needs one uniform
    /// that is already set per draw, and on line work reads the same.
    /// <para>Only the line work is dimmed; the value is exempt (see
    /// <see cref="Build"/>).</para></para>
    /// </summary>
    public static readonly (float R, float G, float B) HiddenColor = (0.28f, 0.30f, 0.36f);

    /// <summary>
    /// How far, in style pixels of world size, <see cref="AnnotationDepth.Occluded"/>
    /// pulls the built overlay toward the eye.
    /// <para><b>It exists because the interesting annotations are COPLANAR with the face
    /// they document.</b> A radial dimension's leader lies exactly in the plane of the
    /// face whose bore it measures, and a callout's arrow touches the surface it points
    /// at; drawn without a bias, those fragments carry the same depth as the triangle
    /// under them up to two different rasterizations' round-off, so a depth-tested
    /// overlay speckles along the whole run instead of reading as "on the face". A bias
    /// toward the eye settles it by DECISION rather than by rounding: coplanar means
    /// visible.</para>
    /// <para>Measured in screen pixels rather than in model units because that is the
    /// one scale-free choice available to a screen-constant overlay — it is 0.09 model
    /// units on a 40 mm plate framed at distance 60 (geometrically nothing, since the
    /// hidden stretches this has to classify are millimetres deep) and over a hundred
    /// depth-buffer bits at every distance a scene is normally framed at, because
    /// depth resolution falls as z-squared while a pixel's world size grows only as z.
    /// </para>
    /// </summary>
    public const double OccludedDepthBiasPx = 1.0;

    // Style, in logical pixels (multiplied by the camera's PixelScale).

    /// <summary>Text height.</summary>
    public const double TextHeightPx = 12;

    /// <summary>Arrowhead length.</summary>
    public const double ArrowLengthPx = 10;

    /// <summary>Arrowhead half-width.</summary>
    public const double ArrowHalfWidthPx = 3.5;

    /// <summary>Gap between model point and extension line.</summary>
    public const double ExtensionGapPx = 4;

    /// <summary>Extension line past the dimension line.</summary>
    public const double ExtensionOvershootPx = 6;

    /// <summary>Gap between a line and its text.</summary>
    public const double TextGapPx = 4;

    /// <summary>Dimension line pulled off the model.</summary>
    public const double DefaultOffsetPx = 40;

    /// <summary>Default leader length.</summary>
    public const double LeaderLengthPx = 36;

    /// <summary>Horizontal leader tail length.</summary>
    public const double TailLengthPx = 14;

    /// <summary>Padding of the datum box around its text.</summary>
    public const double DatumBoxPaddingPx = 4;

    /// <summary>Baseline-to-baseline distance of multi-line text, in text heights.</summary>
    public const double LineSpacing = 1.5;

    /// <summary>Arc chord step of an angular dimension (5 degrees per segment).</summary>
    public const double ArcStepRadians = Math.PI / 36;

    /// <summary>Builds the whole overlay into <paramref name="segments"/> (cleared
    /// first). World-space output; layers draw it with an identity model matrix.</summary>
    /// <param name="segments">Output list, cleared first. Receives the whole overlay when
    /// <paramref name="text"/> is null, and the LINE WORK only when it is not.</param>
    /// <param name="items">Resolved annotations with their instance transforms.</param>
    /// <param name="camera">Billboarding camera.</param>
    /// <param name="depthBiasPx">How far toward the eye to pull the finished overlay, in
    /// style pixels of world size — <see cref="OccludedDepthBiasPx"/> under
    /// <see cref="AnnotationDepth.Occluded"/>, and exactly 0 (an exact-zero semantic
    /// test, so the arithmetic is untouched and the geometry is bit-identical) for the
    /// always-on-top default, which has no depth test for a bias to matter to.</param>
    /// <param name="text">When supplied (cleared first), the stroke-font glyphs and datum
    /// boxes go here instead of into <paramref name="segments"/>, so a front end can draw
    /// the two with different depth behaviour.
    /// <para><b>The split exists because of a MEASUREMENT, not a preference.</b> A
    /// dimension's anatomy is a pointer and a value: the extension lines, dimension line,
    /// leaders and arrowheads say WHERE, and which side of the material they run on is
    /// real information; the text says WHAT, and its 3D position is a placement rather
    /// than a measurement. Depth-treating the whole overlay on the docs plate turned
    /// "40" and "&#x2300;5.5" into smudges — the two figures a reader is there for — while the
    /// lines it dimmed read exactly as intended. So the value is exempt and the pointer
    /// is not; a viewer that hid the number to show where the number was would have
    /// spent the only thing it had.</para></param>
    /// <returns>The number of segments written to <paramref name="segments"/> — the line
    /// work — so a caller that concatenates the two lists into one upload knows where the
    /// text range starts.</returns>
    public static int Build(
        List<(Vector3d A, Vector3d B)> segments, IReadOnlyList<AnnotationItem> items,
        in AnnotationCamera camera, double depthBiasPx = 0,
        List<(Vector3d A, Vector3d B)>? text = null)
    {
        segments.Clear();
        text?.Clear();
        // Null means "one list for everything", which is the incumbent behaviour and
        // keeps the emission ORDER identical - what Pick and every always-on-top draw see.
        var glyphs = text ?? segments;
        foreach (var item in items)
        {
            var annotation = item.Annotation;
            var a = item.World.TransformPoint(annotation.AnchorA);
            var b = item.World.TransformPoint(annotation.AnchorB);
            var offset = item.World.TransformVector(annotation.Offset);
            switch (annotation.Kind)
            {
                case AnnotationKind.LinearDimension:
                    BuildLinear(segments, glyphs, a, b, offset, annotation.Text, camera);
                    break;
                case AnnotationKind.RadialDimension:
                    BuildRadial(segments, glyphs, a, b, offset, annotation.Text, camera);
                    break;
                case AnnotationKind.LeaderNote:
                    BuildLeader(segments, glyphs, a, offset, annotation.Text, boxed: false, camera);
                    break;
                case AnnotationKind.DatumLabel:
                    BuildLeader(segments, glyphs, a, offset, annotation.Text, boxed: true, camera);
                    break;
                case AnnotationKind.AngularDimension:
                    BuildAngular(segments, glyphs, a, b,
                        item.World.TransformPoint(annotation.AnchorC), offset,
                        annotation.Text, camera);
                    break;
            }
        }

        if (depthBiasPx != 0)
        {
            ApplyDepthBias(segments, camera, depthBiasPx);
            if (text is not null)
                ApplyDepthBias(text, camera, depthBiasPx);
        }
        return segments.Count;
    }

    /// <summary>
    /// Pulls every built point toward the eye by <paramref name="depthBiasPx"/> style
    /// pixels of view depth, through <see cref="AnnotationCamera.PulledTowardEye"/> —
    /// which owns the rule that the move must be along the point's own eye ray, so the
    /// overlay's screen position is untouched and only its depth changes.
    /// </summary>
    private static void ApplyDepthBias(
        List<(Vector3d A, Vector3d B)> segments, in AnnotationCamera camera, double depthBiasPx)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            segments[i] = (camera.PulledTowardEye(a, depthBiasPx),
                           camera.PulledTowardEye(b, depthBiasPx));
        }
    }

    /// <summary>Angular dimension: extension rays from the vertex through both ray
    /// points, an arc between them with arrowheads at its ends, degree text outside
    /// the arc's midpoint. The arc radius is the author's offset length when set, else
    /// three quarters of the shorter ray.</summary>
    private static void BuildAngular(
        List<(Vector3d A, Vector3d B)> segments, List<(Vector3d A, Vector3d B)> glyphs,
        in Vector3d a, in Vector3d b,
        in Vector3d vertex, in Vector3d offset, string text, in AnnotationCamera camera)
    {
        var va = a - vertex;
        var vb = b - vertex;
        if (!va.TryNormalize(Tolerance.Default, out var dirA)
            || !vb.TryNormalize(Tolerance.Default, out var dirB))
        {
            AddBillboardText(glyphs, vertex, text, camera);
            return;
        }
        double angle = Math.Acos(Math.Clamp(dirA.Dot(dirB), -1, 1));
        var axisRaw = dirA.Cross(dirB);
        if (!axisRaw.TryNormalize(Tolerance.Default, out var axis))
        {
            // Parallel rays span no plane; the resolver refuses these, so this is
            // belt-and-braces for hand-built resolved annotations.
            AddBillboardText(glyphs, vertex, text, camera);
            return;
        }

        double radius = offset.Length > 0 ? offset.Length : Math.Min(va.Length, vb.Length) * 0.75;
        double overshoot = camera.PxToWorld(ExtensionOvershootPx, vertex);
        double gap = camera.PxToWorld(ExtensionGapPx, vertex);

        // Extension rays from just off the vertex to just past the arc.
        segments.Add((vertex + dirA * gap, vertex + dirA * (radius + overshoot)));
        segments.Add((vertex + dirB * gap, vertex + dirB * (radius + overshoot)));

        // The arc, chorded at ~5 degrees per step.
        int steps = Math.Max(4, (int)Math.Ceiling(angle / ArcStepRadians));
        var previous = vertex + dirA * radius;
        for (int i = 1; i <= steps; i++)
        {
            var point = vertex + Rotate(dirA, axis, angle * i / steps) * radius;
            segments.Add((previous, point));
            previous = point;
        }

        // Arrowheads at the arc ends, wings trailing along the arc (tangent
        // directions; the tangent at parameter t is axis x direction(t)).
        var startTangent = axis.Cross(dirA);
        var endTangent = axis.Cross(dirB);
        AddArrowhead(segments, vertex + dirA * radius, startTangent, dirA, camera);
        AddArrowhead(segments, vertex + dirB * radius, -endTangent, dirB, camera);

        // Degree text outside the arc's midpoint.
        var midDir = Rotate(dirA, axis, angle * 0.5);
        var arcMid = vertex + midDir * radius;
        var textCenter = arcMid + midDir * camera.PxToWorld(TextGapPx + TextHeightPx * 0.5, arcMid);
        AddBillboardText(glyphs, textCenter, text, camera);
    }

    /// <summary>Rodrigues rotation of a vector about a unit axis.</summary>
    private static Vector3d Rotate(in Vector3d v, in Vector3d axis, double angle)
    {
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        return v * cos + axis.Cross(v) * sin + axis * (axis.Dot(v) * (1 - cos));
    }

    /// <summary>Screen-space pick radius for <see cref="Pick"/> (style pixels) — a
    /// line overlay needs a fatter target than its 1-px stroke.</summary>
    public const double PickRadiusPx = 8;

    /// <summary>
    /// Picks the annotation nearest the ray, or −1: each item's own drawn segments are
    /// rebuilt (the same <see cref="Build"/> geometry, so what you see is exactly what
    /// you can click) and the winner is the item whose nearest segment passes within
    /// <paramref name="radiusPx"/> style pixels of the ray at that depth. Depth-blind
    /// on purpose, matching the always-on-top draw: an annotation you can SEE is
    /// pickable even when model geometry sits in front of its anchors.
    /// </summary>
    public static int Pick(
        IReadOnlyList<AnnotationItem> items, in AnnotationCamera camera, in Ray3d ray,
        double radiusPx = PickRadiusPx)
    {
        var scratch = new List<(Vector3d A, Vector3d B)>();
        var one = new AnnotationItem[1];
        int best = -1;
        double bestPx = radiusPx;
        for (int i = 0; i < items.Count; i++)
        {
            one[0] = items[i];
            Build(scratch, one, camera);
            foreach (var (a, b) in scratch)
            {
                double distance = RaySegmentDistance(ray, a, b, out var onSegment);
                double px = distance
                    / (camera.PixelScale * camera.WorldPerPixel(onSegment));
                if (px < bestPx)
                {
                    bestPx = px;
                    best = i;
                }
            }
        }
        return best;
    }

    /// <summary>Closest distance between a ray (t &#x2265; 0) and a segment, with the
    /// segment's closest point out (the depth reference for the pixel conversion).
    /// The clamped two-segment closest-point solve; the parallel guard is a relative
    /// machine-epsilon degeneracy test (scale-free tier).</summary>
    private static double RaySegmentDistance(
        in Ray3d ray, in Vector3d a, in Vector3d b, out Vector3d onSegment)
    {
        var d1 = ray.Direction;
        var d2 = b - a;
        var w = ray.Origin - a;
        double a11 = d1.Dot(d1);
        double a22 = d2.Dot(d2);
        double a12 = d1.Dot(d2);
        double b1 = d1.Dot(w);
        double b2 = d2.Dot(w);
        double denom = a11 * a22 - a12 * a12;

        double s = denom > 1e-14 * a11 * a22 ? Math.Max(0, (a12 * b2 - a22 * b1) / denom) : 0;
        double t = a22 > 0 ? Math.Clamp((b2 + s * a12) / a22, 0, 1) : 0;
        s = a11 > 0 ? Math.Max(0, (t * a12 - b1) / a11) : 0;
        t = a22 > 0 ? Math.Clamp((b2 + s * a12) / a22, 0, 1) : 0;

        onSegment = a + d2 * t;
        return (ray.Origin + d1 * s).DistanceTo(onSegment);
    }

    /// <summary>The classic dimension: extension lines from the anchors, a dimension
    /// line between them with arrowheads at both ends, text centered above it.</summary>
    private static void BuildLinear(
        List<(Vector3d A, Vector3d B)> segments, List<(Vector3d A, Vector3d B)> glyphs,
        in Vector3d a, in Vector3d b,
        in Vector3d offset, string text, in AnnotationCamera camera)
    {
        var mid = (a + b) * 0.5;
        var span = b - a;
        if (!span.TryNormalize(Tolerance.Default, out var measureDir))
        {
            // Degenerate (zero-length) dimension: just show the text at the point.
            AddBillboardText(glyphs, mid, text, camera);
            return;
        }

        // Placement: the author's offset with its along-measure component removed
        // (extension lines must be perpendicular to the dimension line); when absent
        // or degenerate, a screen-space default perpendicular to the measured span.
        var perpOffset = offset - measureDir * offset.Dot(measureDir);
        Vector3d offDir;
        double offLen;
        if (perpOffset.TryNormalize(Tolerance.Default, out var authorDir))
        {
            offDir = authorDir;
            offLen = perpOffset.Length;
        }
        else
        {
            var screenPerp = measureDir.Cross(camera.Forward);
            if (!screenPerp.TryNormalize(Tolerance.Default, out offDir))
                offDir = camera.Up;   // measuring along the view axis: any screen direction works
            offLen = camera.PxToWorld(DefaultOffsetPx, mid);
        }

        var a2 = a + offDir * offLen;
        var b2 = b + offDir * offLen;

        // Extension lines: small gap off the model, small overshoot past the line.
        segments.Add((a + offDir * camera.PxToWorld(ExtensionGapPx, a),
                      a2 + offDir * camera.PxToWorld(ExtensionOvershootPx, a2)));
        segments.Add((b + offDir * camera.PxToWorld(ExtensionGapPx, b),
                      b2 + offDir * camera.PxToWorld(ExtensionOvershootPx, b2)));

        // Dimension line + inward-pointing arrowheads at both ends.
        segments.Add((a2, b2));
        AddArrowhead(segments, a2, measureDir, offDir, camera);
        AddArrowhead(segments, b2, -measureDir, offDir, camera);

        // Text centered above the dimension line (along the offset direction).
        var textCenter = (a2 + b2) * 0.5
            + offDir * camera.PxToWorld(TextGapPx + TextHeightPx * 0.5, (a2 + b2) * 0.5);
        AddBillboardText(glyphs, textCenter, text, camera);
    }

    /// <summary>Radius/diameter dimension: arrow touching the circle pointing at the
    /// center, a radial leader outward, a short horizontal tail, text at its end.</summary>
    private static void BuildRadial(
        List<(Vector3d A, Vector3d B)> segments, List<(Vector3d A, Vector3d B)> glyphs,
        in Vector3d onCircle, in Vector3d center,
        in Vector3d offset, string text, in AnnotationCamera camera)
    {
        var radial = onCircle - center;
        if (!radial.TryNormalize(Tolerance.Default, out var outward))
            outward = camera.Right;

        // Arrow tip on the circle, pointing inward (wings trail outward).
        AddArrowhead(segments, onCircle, outward, PerpendicularOnScreen(outward, camera), camera);

        // Leader: outward along the radial (or the author's offset), then a tail
        // toward the horizontal text.
        var elbow = offset.LengthSquared > 0
            ? onCircle + offset
            : onCircle + outward * camera.PxToWorld(LeaderLengthPx, onCircle);
        segments.Add((onCircle, elbow));
        FinishLeaderText(segments, glyphs, elbow, outward, text, boxed: false, camera);
    }

    /// <summary>Leader note / datum: arrow at the anchor, leader to the text
    /// (screen-space up-right by default), optional datum box around the text.</summary>
    private static void BuildLeader(
        List<(Vector3d A, Vector3d B)> segments, List<(Vector3d A, Vector3d B)> glyphs,
        in Vector3d anchor,
        in Vector3d offset, string text, bool boxed, in AnnotationCamera camera)
    {
        var leader = offset.LengthSquared > 0
            ? offset
            : (camera.Right + camera.Up).Normalized() * camera.PxToWorld(LeaderLengthPx, anchor);
        var elbow = anchor + leader;
        var leaderDir = leader.Normalized();

        AddArrowhead(segments, anchor, leaderDir, PerpendicularOnScreen(leaderDir, camera), camera);
        segments.Add((anchor, elbow));
        FinishLeaderText(segments, glyphs, elbow, leaderDir, text, boxed, camera);
    }

    /// <summary>Shared leader ending: a short horizontal tail on the side the leader
    /// leans toward, then the text (and its datum box when <paramref name="boxed"/>).
    /// Multi-line text (split on '\n') stacks downward from the tail line, left-aligned
    /// on the right side and right-aligned against the tail on the left side, with the
    /// box (when boxed) spanning every line.</summary>
    private static void FinishLeaderText(
        List<(Vector3d A, Vector3d B)> segments, List<(Vector3d A, Vector3d B)> glyphs,
        in Vector3d elbow, in Vector3d leaderDir,
        string text, bool boxed, in AnnotationCamera camera)
    {
        double side = leaderDir.Dot(camera.Right) < 0 ? -1 : 1;
        var tailEnd = elbow + camera.Right * (side * camera.PxToWorld(TailLengthPx, elbow));
        segments.Add((elbow, tailEnd));

        string[] lines = text.Split('\n');
        double height = camera.PxToWorld(TextHeightPx, tailEnd);
        double lineStep = height * LineSpacing;
        double maxWidth = 0;
        foreach (string line in lines)
            maxWidth = Math.Max(maxWidth, StrokeFont.TextWidth(line) * height);
        double gap = camera.PxToWorld(TextGapPx, tailEnd);

        // First line vertically centered on the tail; the rest stack below it.
        for (int i = 0; i < lines.Length; i++)
        {
            double lineWidth = StrokeFont.TextWidth(lines[i]) * height;
            var lineOrigin = (side > 0
                    ? tailEnd + camera.Right * gap
                    : tailEnd - camera.Right * (gap + lineWidth))
                - camera.Up * (height * 0.5 + i * lineStep);
            StrokeFont.AppendText(glyphs, lines[i], lineOrigin, camera.Right, camera.Up, height);
        }

        if (boxed)
        {
            double pad = camera.PxToWorld(DatumBoxPaddingPx, tailEnd);
            // The box's baseline-left origin is the LAST line's leftmost origin; its
            // height spans from that baseline up to the first line's cap height.
            var boxOrigin = (side > 0
                    ? tailEnd + camera.Right * gap
                    : tailEnd - camera.Right * (gap + maxWidth))
                - camera.Up * (height * 0.5 + (lines.Length - 1) * lineStep);
            double boxHeight = height + (lines.Length - 1) * lineStep;
            AddBox(glyphs, boxOrigin, camera.Right, camera.Up, maxWidth, boxHeight, pad);
        }
    }

    /// <summary>Billboarded text centered at a world point, sized in screen pixels;
    /// multi-line text (split on '\n') is stacked and centered as a block, each line
    /// centered within it.</summary>
    private static void AddBillboardText(
        List<(Vector3d A, Vector3d B)> glyphs, in Vector3d center, string text,
        in AnnotationCamera camera)
    {
        string[] lines = text.Split('\n');
        double height = camera.PxToWorld(TextHeightPx, center);
        double lineStep = height * LineSpacing;
        double blockHeight = height + (lines.Length - 1) * lineStep;
        for (int i = 0; i < lines.Length; i++)
        {
            double width = StrokeFont.TextWidth(lines[i]) * height;
            var origin = center - camera.Right * (width * 0.5)
                + camera.Up * (blockHeight * 0.5 - height - i * lineStep);
            StrokeFont.AppendText(glyphs, lines[i], origin, camera.Right, camera.Up, height);
        }
    }

    /// <summary>A V arrowhead: tip at <paramref name="tip"/>, wings trailing along
    /// <paramref name="direction"/> spread by <paramref name="perpendicular"/>.</summary>
    private static void AddArrowhead(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d tip, in Vector3d direction,
        in Vector3d perpendicular, in AnnotationCamera camera)
    {
        double length = camera.PxToWorld(ArrowLengthPx, tip);
        double halfWidth = camera.PxToWorld(ArrowHalfWidthPx, tip);
        var back = tip + direction * length;
        segments.Add((tip, back + perpendicular * halfWidth));
        segments.Add((tip, back - perpendicular * halfWidth));
    }

    /// <summary>A rectangle around a laid-out text run (the datum box), padded.</summary>
    private static void AddBox(
        List<(Vector3d A, Vector3d B)> glyphs, in Vector3d origin,
        in Vector3d right, in Vector3d up, double width, double height, double pad)
    {
        var bl = origin - right * pad - up * pad;
        var br = origin + right * (width + pad) - up * pad;
        var tr = origin + right * (width + pad) + up * (height + pad);
        var tl = origin - right * pad + up * (height + pad);
        glyphs.Add((bl, br));
        glyphs.Add((br, tr));
        glyphs.Add((tr, tl));
        glyphs.Add((tl, bl));
    }

    /// <summary>A screen-plane direction perpendicular to <paramref name="direction"/>
    /// (arrow wings should read as wings from the current viewpoint).</summary>
    private static Vector3d PerpendicularOnScreen(in Vector3d direction, in AnnotationCamera camera)
    {
        var perpendicular = direction.Cross(camera.Forward);
        return perpendicular.TryNormalize(Tolerance.Default, out var unit) ? unit : camera.Up;
    }
}
