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
}

/// <summary>
/// Pure geometry for the annotation overlay (no GL — unit-testable): builds the line
/// segments of dimension lines, extension lines, arrowheads, leaders, datum boxes,
/// and stroke-font text, billboarded to the camera and sized in screen pixels.
/// </summary>
public static class AnnotationGeometry
{
    /// <summary>The overlay's line colour, shared by every front end (a second
    /// definition of what a dimension looks like would drift).</summary>
    public static readonly (float R, float G, float B) Color = (0.92f, 0.93f, 0.97f);

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
    public static void Build(
        List<(Vector3d A, Vector3d B)> segments, IReadOnlyList<AnnotationItem> items,
        in AnnotationCamera camera)
    {
        segments.Clear();
        foreach (var item in items)
        {
            var annotation = item.Annotation;
            var a = item.World.TransformPoint(annotation.AnchorA);
            var b = item.World.TransformPoint(annotation.AnchorB);
            var offset = item.World.TransformVector(annotation.Offset);
            switch (annotation.Kind)
            {
                case AnnotationKind.LinearDimension:
                    BuildLinear(segments, a, b, offset, annotation.Text, camera);
                    break;
                case AnnotationKind.RadialDimension:
                    BuildRadial(segments, a, b, offset, annotation.Text, camera);
                    break;
                case AnnotationKind.LeaderNote:
                    BuildLeader(segments, a, offset, annotation.Text, boxed: false, camera);
                    break;
                case AnnotationKind.DatumLabel:
                    BuildLeader(segments, a, offset, annotation.Text, boxed: true, camera);
                    break;
                case AnnotationKind.AngularDimension:
                    BuildAngular(segments, a, b,
                        item.World.TransformPoint(annotation.AnchorC), offset,
                        annotation.Text, camera);
                    break;
            }
        }
    }

    /// <summary>Angular dimension: extension rays from the vertex through both ray
    /// points, an arc between them with arrowheads at its ends, degree text outside
    /// the arc's midpoint. The arc radius is the author's offset length when set, else
    /// three quarters of the shorter ray.</summary>
    private static void BuildAngular(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d a, in Vector3d b,
        in Vector3d vertex, in Vector3d offset, string text, in AnnotationCamera camera)
    {
        var va = a - vertex;
        var vb = b - vertex;
        if (!va.TryNormalize(Tolerance.Default, out var dirA)
            || !vb.TryNormalize(Tolerance.Default, out var dirB))
        {
            AddBillboardText(segments, vertex, text, camera);
            return;
        }
        double angle = Math.Acos(Math.Clamp(dirA.Dot(dirB), -1, 1));
        var axisRaw = dirA.Cross(dirB);
        if (!axisRaw.TryNormalize(Tolerance.Default, out var axis))
        {
            // Parallel rays span no plane; the resolver refuses these, so this is
            // belt-and-braces for hand-built resolved annotations.
            AddBillboardText(segments, vertex, text, camera);
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
        AddBillboardText(segments, textCenter, text, camera);
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
        List<(Vector3d A, Vector3d B)> segments, in Vector3d a, in Vector3d b,
        in Vector3d offset, string text, in AnnotationCamera camera)
    {
        var mid = (a + b) * 0.5;
        var span = b - a;
        if (!span.TryNormalize(Tolerance.Default, out var measureDir))
        {
            // Degenerate (zero-length) dimension: just show the text at the point.
            AddBillboardText(segments, mid, text, camera);
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
        AddBillboardText(segments, textCenter, text, camera);
    }

    /// <summary>Radius/diameter dimension: arrow touching the circle pointing at the
    /// center, a radial leader outward, a short horizontal tail, text at its end.</summary>
    private static void BuildRadial(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d onCircle, in Vector3d center,
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
        FinishLeaderText(segments, elbow, outward, text, boxed: false, camera);
    }

    /// <summary>Leader note / datum: arrow at the anchor, leader to the text
    /// (screen-space up-right by default), optional datum box around the text.</summary>
    private static void BuildLeader(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d anchor,
        in Vector3d offset, string text, bool boxed, in AnnotationCamera camera)
    {
        var leader = offset.LengthSquared > 0
            ? offset
            : (camera.Right + camera.Up).Normalized() * camera.PxToWorld(LeaderLengthPx, anchor);
        var elbow = anchor + leader;
        var leaderDir = leader.Normalized();

        AddArrowhead(segments, anchor, leaderDir, PerpendicularOnScreen(leaderDir, camera), camera);
        segments.Add((anchor, elbow));
        FinishLeaderText(segments, elbow, leaderDir, text, boxed, camera);
    }

    /// <summary>Shared leader ending: a short horizontal tail on the side the leader
    /// leans toward, then the text (and its datum box when <paramref name="boxed"/>).
    /// Multi-line text (split on '\n') stacks downward from the tail line, left-aligned
    /// on the right side and right-aligned against the tail on the left side, with the
    /// box (when boxed) spanning every line.</summary>
    private static void FinishLeaderText(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d elbow, in Vector3d leaderDir,
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
            StrokeFont.AppendText(segments, lines[i], lineOrigin, camera.Right, camera.Up, height);
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
            AddBox(segments, boxOrigin, camera.Right, camera.Up, maxWidth, boxHeight, pad);
        }
    }

    /// <summary>Billboarded text centered at a world point, sized in screen pixels;
    /// multi-line text (split on '\n') is stacked and centered as a block, each line
    /// centered within it.</summary>
    private static void AddBillboardText(
        List<(Vector3d A, Vector3d B)> segments, in Vector3d center, string text,
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
            StrokeFont.AppendText(segments, lines[i], origin, camera.Right, camera.Up, height);
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
        List<(Vector3d A, Vector3d B)> segments, in Vector3d origin,
        in Vector3d right, in Vector3d up, double width, double height, double pad)
    {
        var bl = origin - right * pad - up * pad;
        var br = origin + right * (width + pad) - up * pad;
        var tr = origin + right * (width + pad) + up * (height + pad);
        var tl = origin - right * pad + up * (height + pad);
        segments.Add((bl, br));
        segments.Add((br, tr));
        segments.Add((tr, tl));
        segments.Add((tl, bl));
    }

    /// <summary>A screen-plane direction perpendicular to <paramref name="direction"/>
    /// (arrow wings should read as wings from the current viewpoint).</summary>
    private static Vector3d PerpendicularOnScreen(in Vector3d direction, in AnnotationCamera camera)
    {
        var perpendicular = direction.Cross(camera.Forward);
        return perpendicular.TryNormalize(Tolerance.Default, out var unit) ? unit : camera.Up;
    }
}
