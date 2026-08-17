using EngrCAD.Core;

namespace EngrCAD.Viewer;

/// <summary>
/// A social-video export preset: frame size and rate, the platform's DURATION CAP, and
/// its SAFE AREA — the fraction of each edge the platform's own captions and UI overlay
/// cover, inside which the model must stay to be seen. The safe area is real geometry
/// here, not decoration: <see cref="ReelFraming.CameraFor"/> frames INTO it and the
/// export refuses a clip the platform would cut.
/// <para>The inset figures are nominal transcriptions of the platforms' published
/// guidance (⚠ verify-against-datasheet, the <c>StandardHoles</c> convention): both
/// portrait platforms overlay roughly the bottom 15% (captions, progress) and the right
/// edge (like/share rail).</para>
/// </summary>
/// <param name="Name">The platform preset's name — what a refusal names.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Fps">Frames per second the platform plays at.</param>
/// <param name="MaxDurationSeconds">The platform's clip cap; ∞ for none.</param>
/// <param name="SafeLeft">Left inset as a fraction of the width.</param>
/// <param name="SafeRight">Right inset as a fraction of the width.</param>
/// <param name="SafeTop">Top inset as a fraction of the height.</param>
/// <param name="SafeBottom">Bottom inset as a fraction of the height.</param>
public sealed record ReelFormat(
    string Name, int Width, int Height, int Fps, double MaxDurationSeconds,
    double SafeLeft, double SafeRight, double SafeTop, double SafeBottom)
{
    /// <summary>Frame aspect ratio (width over height — under 1 for portrait).</summary>
    public double Aspect => (double)Width / Height;

    /// <summary>Instagram Reels: portrait 1080×1920 at 30 fps, capped at 90 s.</summary>
    public static ReelFormat InstagramReel { get; } =
        new("Instagram Reel", 1080, 1920, 30, 90, 0.05, 0.10, 0.05, 0.15);

    /// <summary>YouTube Shorts: portrait 1080×1920 at 30 fps, capped at 180 s.</summary>
    public static ReelFormat YouTubeShort { get; } =
        new("YouTube Short", 1080, 1920, 30, 180, 0.05, 0.10, 0.05, 0.15);

    /// <summary>The lighter portrait tier (720×1280), Reel-capped.</summary>
    public static ReelFormat Portrait720 { get; } =
        new("Portrait 720", 720, 1280, 30, 90, 0.05, 0.10, 0.05, 0.15);

    /// <summary>Standard landscape YouTube 1080p — the same knob sideways, uncapped,
    /// with only a thin margin (no caption rail lives over landscape player chrome
    /// until the controls appear).</summary>
    public static ReelFormat YouTubeStandard { get; } =
        new("YouTube 1080p", 1920, 1080, 30, double.PositiveInfinity, 0.03, 0.03, 0.03, 0.06);

    /// <summary>
    /// The documented ffmpeg recipe turning an exported frame sequence into the MP4 the
    /// platforms actually want — the honest route, and a MEASURED one: the dependency-free
    /// alternative (Motion-JPEG-in-MP4) is refused by Chrome, Edge and the Windows media
    /// stack while a hand-rolled H.264 encoder stays a product-sized campaign (design.md
    /// §6b records the assessment), and GIF/APNG are transcoded
    /// or rejected by both platforms. <c>yuv420p</c> is what makes the file play
    /// everywhere; the scale filter is a no-op on frames this export rendered (already
    /// at the preset size) and a safety net on foreign ones.
    /// </summary>
    public string FfmpegCommand(string pattern = "frame-%04d.png", string output = "reel.mp4") =>
        $"ffmpeg -framerate {Fps} -i {pattern} -c:v libx264 -pix_fmt yuv420p "
        + $"-vf \"scale={Width}:{Height}\" {output}";
}

/// <summary>
/// Frames a model INTO a <see cref="ReelFormat"/>'s safe area — the aspect-aware
/// framing a portrait export needs, because the default camera frames a landscape-ish
/// viewport and letterboxes a tall one.
/// </summary>
public static class ReelFraming
{
    /// <summary>The perspective field of view the framing solves against —
    /// <see cref="CameraMath.FovY"/>, the renderer's own, by reference rather than by a
    /// second literal (a framing solved against a different frustum would be a claim
    /// about a different camera).</summary>
    public static double FovY => CameraMath.FovY;

    /// <summary>
    /// The camera that FILLS <paramref name="format"/>'s safe area with
    /// <paramref name="bounds"/> at the house iso orientation: the smallest distance at
    /// which all eight corners project inside the safe rectangle, with the target
    /// shifted so the projected bounds CENTRE in it (an asymmetric safe area — captions
    /// below, rail right — wants the model up and left of the frame centre).
    /// <para>Deterministic and closed-form per iteration: each corner's NDC coordinate
    /// is <c>a / (D + w)</c> with a and w fixed by the orientation, monotone in the
    /// distance D, so the minimal D is a max over per-corner solutions; the centring
    /// shift is depth-second-order, so two rounds settle it to round-off.</para>
    /// </summary>
    public static CameraState CameraFor(in Aabb bounds, ReelFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (bounds.IsEmpty)
            return CameraMath.DefaultCamera(bounds);

        const double yaw = 0.7, pitch = 0.45;   // DefaultCamera's iso pose
        // Orientation basis (independent of distance): forward from eye toward target,
        // right and up completing it — LookAt's own construction, up = +Z.
        var eyeDirection = (CameraMath.Eye(yaw, pitch, 1.0, Vector3d.Zero)).Normalized();
        var forward = -eyeDirection;
        var right = forward.Cross(Vector3d.UnitZ).Normalized();
        var up = right.Cross(forward);

        // Safe rectangle in NDC. A fraction f of the frame's left edge is x ∈ [-1, -1+2f].
        double xMin = -1 + 2 * format.SafeLeft;
        double xMax = 1 - 2 * format.SafeRight;
        double yMax = 1 - 2 * format.SafeTop;
        double yMin = -1 + 2 * format.SafeBottom;
        double kx = 1.0 / Math.Tan(FovY / 2) / format.Aspect;
        double ky = 1.0 / Math.Tan(FovY / 2);

        var corners = Corners(bounds);
        var target = bounds.Center;
        // Every corner must stay in front of the eye with margin. The exact form, not a
        // blanket size multiple: a blanket floor larger than the projection constraints
        // parks the camera too far and the safe area stops being FILLED (measured: a
        // landscape 60x20x10 box filled only 0.70 of its safe rect under a
        // diagonal-length floor). The nearest corner's depth is unchanged by the
        // centring shift (the shift is perpendicular to forward), so one computation
        // serves every round.
        double nearestDepth = 0;
        foreach (var corner in corners)
            nearestDepth = Math.Max(nearestDepth, -forward.Dot(corner - target));
        double floor = Math.Max(nearestDepth * 1.1, 1e-6);
        double distance = floor;

        // The shift's error is first-order in (depth spread / distance) — up to ~0.4
        // for a deep model framed close — so the round budget is sized for the slowest
        // real contraction (0.4^20 ~ 1e-8 NDC) rather than the typical one; each round
        // is eight corners of arithmetic, so the budget costs nothing.
        for (int round = 0; round < 20; round++)
        {
            // Minimal distance meeting every corner's four edge constraints.
            distance = floor;
            foreach (var corner in corners)
            {
                var v = corner - target;
                double x = right.Dot(v), y = up.Dot(v), w = forward.Dot(v);
                if (x != 0)
                    distance = Math.Max(distance, kx * x / (x > 0 ? xMax : xMin) - w);
                if (y != 0)
                    distance = Math.Max(distance, ky * y / (y > 0 ? yMax : yMin) - w);
            }

            // Centre the achieved projected box in the safe rectangle by shifting the
            // TARGET in view space (the orbit camera has no principal-point offset, so
            // the target shift is the one lever). The shift changes each corner's depth
            // not at all and its NDC only through the fixed numerator, so the next
            // round's re-solve converges.
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            foreach (var corner in corners)
            {
                var v = corner - target;
                double d = distance + forward.Dot(v);
                double nx = kx * right.Dot(v) / d;
                double ny = ky * up.Dot(v) / d;
                minX = Math.Min(minX, nx); maxX = Math.Max(maxX, nx);
                minY = Math.Min(minY, ny); maxY = Math.Max(maxY, ny);
            }
            double shiftX = ((minX + maxX) / 2 - (xMin + xMax) / 2) * distance / kx;
            double shiftY = ((minY + maxY) / 2 - (yMin + yMax) / 2) * distance / ky;
            if (Math.Abs(shiftX) < 1e-12 * distance && Math.Abs(shiftY) < 1e-12 * distance)
                break;
            target += right * shiftX + up * shiftY;
        }

        // One final solve from the settled target, so a round-limited exit cannot leave
        // the distance a shift behind the target it was solved for.
        distance = floor;
        foreach (var corner in corners)
        {
            var v = corner - target;
            double x = right.Dot(v), y = up.Dot(v), w = forward.Dot(v);
            if (x != 0)
                distance = Math.Max(distance, kx * x / (x > 0 ? xMax : xMin) - w);
            if (y != 0)
                distance = Math.Max(distance, ky * y / (y > 0 ? yMax : yMin) - w);
        }

        return new CameraState(yaw, pitch, distance, target);
    }

    /// <summary>
    /// Draws <paramref name="format"/>'s safe rectangle into an RGBA frame in place — a
    /// 2-pixel amber outline where the platform's UI begins, for PREVIEW posters only
    /// (an exported clip never carries it; the overlay is a proofing aid, not content).
    /// CPU-side on the finished pixels, deliberately: no shader, no per-front-end
    /// plumbing, and a poster with the overlay differs from one without by exactly the
    /// rectangle.
    /// </summary>
    public static void DrawSafeArea(byte[] rgbaTopDown, int width, int height, ReelFormat format)
    {
        ArgumentNullException.ThrowIfNull(rgbaTopDown);
        ArgumentNullException.ThrowIfNull(format);
        if (rgbaTopDown.Length != width * height * 4)
            throw new ArgumentException(
                $"Pixel buffer is {rgbaTopDown.Length} bytes for a {width}x{height} RGBA frame "
                + $"(expected {width * height * 4}).", nameof(rgbaTopDown));

        int left = (int)Math.Round(width * format.SafeLeft);
        int rightEdge = width - 1 - (int)Math.Round(width * format.SafeRight);
        int top = (int)Math.Round(height * format.SafeTop);
        int bottom = height - 1 - (int)Math.Round(height * format.SafeBottom);

        for (int t = 0; t < 2; t++)
        {
            for (int x = left; x <= rightEdge; x++)
            {
                Plot(rgbaTopDown, width, height, x, top + t);
                Plot(rgbaTopDown, width, height, x, bottom - t);
            }
            for (int y = top; y <= bottom; y++)
            {
                Plot(rgbaTopDown, width, height, left + t, y);
                Plot(rgbaTopDown, width, height, rightEdge - t, y);
            }
        }
    }

    private static void Plot(byte[] pixels, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
            return;
        int at = (y * width + x) * 4;
        pixels[at] = 235;      // amber — nothing else in a shaded frame is this colour
        pixels[at + 1] = 168;
        pixels[at + 2] = 36;
        pixels[at + 3] = 255;
    }

    private static Vector3d[] Corners(in Aabb bounds) =>
    [
        new(bounds.Min.X, bounds.Min.Y, bounds.Min.Z),
        new(bounds.Max.X, bounds.Min.Y, bounds.Min.Z),
        new(bounds.Min.X, bounds.Max.Y, bounds.Min.Z),
        new(bounds.Max.X, bounds.Max.Y, bounds.Min.Z),
        new(bounds.Min.X, bounds.Min.Y, bounds.Max.Z),
        new(bounds.Max.X, bounds.Min.Y, bounds.Max.Z),
        new(bounds.Min.X, bounds.Max.Y, bounds.Max.Z),
        new(bounds.Max.X, bounds.Max.Y, bounds.Max.Z),
    ];
}
