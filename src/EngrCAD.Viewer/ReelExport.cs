using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// What a reel export hands back: the frames, the ffmpeg line that turns them into the
/// platform's MP4, the camera the clip was framed with, and the aliasing measurement —
/// the fastest per-frame body rotation, which is what decides whether the clip can be
/// believed at the platform's frame rate (the gear-clip lesson: a planetary set at 24
/// frames read its sun turning SLOWER than the carrier driving it).
/// </summary>
/// <param name="FramePaths">The written frame files, in order.</param>
/// <param name="FfmpegCommand">The exact encode line for these frames.</param>
/// <param name="Frames">How many frames were rendered (duration × fps).</param>
/// <param name="Camera">The safe-area framing used (when no camera track drove it).</param>
/// <param name="MaxRotationPerFrameRadians">The largest rigid rotation any instance
/// makes between consecutive frames. Body-level ONLY, honestly: tooth-level detail
/// aliases far earlier than the body does (a tooth's period is a pitch, not a turn),
/// and no generic check can know a part's tooth count — the caller with a gear in
/// frame checks pitch advance the way <c>docs/examples/gears.md</c> does.</param>
public sealed record ReelExportResult(
    IReadOnlyList<string> FramePaths, string FfmpegCommand, int Frames,
    CameraState Camera, double MaxRotationPerFrameRadians)
{
    /// <summary>How much SLOWER the animation must run for the fastest body to stay
    /// under <paramref name="maxPerFrameRadians"/> per frame — 1 when it already does.
    /// The number to state in a caption when a mechanism is deliberately shown slowed
    /// (the honest-reading rule: say which property was given up).</summary>
    public double SlowdownFactorFor(double maxPerFrameRadians = Math.PI / 8) =>
        maxPerFrameRadians <= 0 || MaxRotationPerFrameRadians <= maxPerFrameRadians
            ? 1
            : MaxRotationPerFrameRadians / maxPerFrameRadians;
}

/// <summary>
/// Social-video (Reel/Short) export: a COMPOSITION over machinery that already exists —
/// <c>Animation.At</c> is pure, <c>RenderSequence</c> batches a clip through one
/// context, <c>AnimationExport.RenderFrames</c> writes the sequence — plus the three
/// things a platform preset genuinely adds: safe-area FRAMING, the duration cap as a
/// REFUSAL (never a silent trim), and the aliasing check as a measurement.
/// </summary>
public static class ReelExport
{
    /// <summary>
    /// Renders <paramref name="animation"/> as a frame sequence at
    /// <paramref name="format"/>'s size and rate, framed into its safe area, and
    /// returns the frames with the ffmpeg line that finishes the job (MP4/H.264 is
    /// what the platforms want, and the frame sequence + ffmpeg is the honest
    /// dependency-free route — see <see cref="ReelFormat.FfmpegCommand"/>).
    /// <para>Refusals are the feature: a clip past the platform's cap is refused
    /// NAMING the platform and both durations (a silent trim would ship a clip the
    /// platform cuts mid-motion), and a body turning past π per frame is refused as
    /// unrepresentable (beyond Nyquist the rotation's very direction is gone — no
    /// frame rate the platform plays can fix it; slow the animation instead). Below
    /// that hard limit the measured rotation rides the result, with
    /// <see cref="ReelExportResult.SlowdownFactorFor"/> giving the caption number.</para>
    /// </summary>
    public static ReelExportResult RenderReel(
        this Animation animation, Scene scene, string directory, ReelFormat format,
        ViewStyle style = ViewStyle.ShadedWithEdges, bool loop = true,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(format);

        if (animation.Duration > format.MaxDurationSeconds)
            throw new ArgumentException(
                $"{format.Name} caps a clip at {format.MaxDurationSeconds:0} s and this animation "
                + $"runs {animation.Duration:0.##} s. Shorten the animation rather than letting "
                + "the platform cut it mid-motion.", nameof(animation));

        int frames = Math.Max(2, (int)Math.Round(animation.Duration * format.Fps));
        double maxTurn = MaxRotationPerFrame(animation, scene, frames, loop);
        if (maxTurn > Math.PI * (1 - 1e-12))
            throw new InvalidOperationException(
                $"A body turns {maxTurn:0.00} rad between consecutive frames at {format.Fps} fps — "
                + "at or past Nyquist (π), where even the direction of rotation is not "
                + $"represented. Slow the animation by at least {maxTurn / (Math.PI / 2):0.0}× "
                + "(and state the slowdown in the caption), or shorten what it shows.");

        var camera = ReelFraming.CameraFor(ClipBounds(animation, scene), format);
        var paths = animation.RenderFrames(
            scene, directory, frames, format.Width, format.Height, camera, style, loop,
            ambientOcclusion);
        return new ReelExportResult(paths, format.FfmpegCommand(), frames, camera, maxTurn);
    }

    /// <summary>
    /// One PNG still of the clip at timeline <paramref name="t"/>, at the preset's size
    /// and framing, with the safe-area rectangle drawn over it when
    /// <paramref name="safeArea"/> — the proofing poster that shows where the platform's
    /// captions and rail will land BEFORE ninety frames are spent. The overlay is
    /// CPU-drawn on the finished pixels (see <see cref="ReelFraming.DrawSafeArea"/>),
    /// so with it off the poster is exactly frame ⌊t·N⌋ of the export.
    /// </summary>
    public static void RenderReelPoster(
        this Animation animation, Scene scene, double t, string path, ReelFormat format,
        bool safeArea = true, ViewStyle style = ViewStyle.ShadedWithEdges,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(path);

        var instances = EngrCad.PoseAt(scene, animation, t);
        var sample = animation.At(Math.Clamp(t, 0, 1));
        var camera = sample.Camera ?? ReelFraming.CameraFor(ClipBounds(animation, scene), format);
        var pixels = OffscreenRenderer.Render(
            instances, format.Width, format.Height, camera, furniture: true, style,
            ambientOcclusion: ambientOcclusion,
            fieldStep: sample.FieldName is { } stepField && animation.FieldTrack is { } fieldTrack
                ? (fieldTrack, stepField) : null);
        if (safeArea)
            ReelFraming.DrawSafeArea(pixels, format.Width, format.Height, format);
        PngWriter.Write(path, pixels, format.Width, format.Height);
    }

    /// <summary>The bounds a clip is framed over: the union of the first and last
    /// frames' instances — <c>AnimationExport</c>'s own rule, never per-frame framing.</summary>
    private static Aabb ClipBounds(Animation animation, Scene scene)
    {
        var bounds = Aabb.Empty;
        foreach (var instance in EngrCad.PoseAt(scene, animation, 0))
            bounds = bounds.Union(instance.Bounds());
        foreach (var instance in EngrCad.PoseAt(scene, animation, 1))
            bounds = bounds.Union(instance.Bounds());
        return bounds;
    }

    /// <summary>
    /// The largest rigid rotation any instance makes between consecutive frames,
    /// matched by occurrence path (the <c>PoseByPath</c> rule — an instance a frame
    /// says nothing about has not moved). A pure translation measures exactly zero
    /// however fast: translation does not alias into reversed motion the way rotation
    /// does.
    /// <para><b>The measure samples at HALF steps, because a matrix delta itself folds
    /// at π</b> — a 4.2 rad step and a −2.1 rad step are the SAME rotation matrix, so a
    /// whole-step reading can never exceed Nyquist and the refusal it feeds would be
    /// unreachable (found by the test that expected it to fire). Summing the two
    /// half-step principal angles reads the true advance up to 2π per frame; an exact
    /// 2π per frame is genuinely invisible to ANY sampling measure, which is the honest
    /// boundary rather than a gap.</para>
    /// </summary>
    internal static double MaxRotationPerFrame(
        Animation animation, Scene scene, int frames, bool loop)
    {
        double maxTurn = 0;
        Dictionary<string, Matrix4d>? previous = null;
        double previousHalf = 0;
        int halfSteps = 2 * (loop ? frames : frames - 1);
        for (int i = 0; i <= halfSteps; i++)
        {
            double t = (double)i / halfSteps * (loop ? 1 : 1);
            var current = new Dictionary<string, Matrix4d>();
            foreach (var instance in EngrCad.PoseAt(scene, animation, Math.Min(t, 1)))
                current[instance.Path] = instance.World;
            double half = 0;
            if (previous is not null)
            {
                foreach (var (path, world) in current)
                {
                    if (previous.TryGetValue(path, out var before))
                        half = Math.Max(half, RotationAngle(before, world));
                }
                // A frame's advance is the sum of its two half-steps; odd i closes one.
                if (i % 2 == 0)
                    maxTurn = Math.Max(maxTurn, previousHalf + half);
            }
            previousHalf = half;
            previous = current;
        }
        return maxTurn;
    }

    /// <summary>The rotation angle between two rigid placements — the angle of
    /// b·a⁻¹'s rotation part, via the trace (|trace(R)| ≤ 3 with equality at the
    /// identity; the clamp only trims round-off).</summary>
    private static double RotationAngle(in Matrix4d a, in Matrix4d b)
    {
        // Rotation columns of each placement (rigid poses — orthonormal by construction).
        // trace(Rb·Raᵀ) = Σ column_i(b)·column_i(a).
        double trace = 0;
        for (int c = 0; c < 3; c++)
        {
            var ca = Column(a, c);
            var cb = Column(b, c);
            // Normalize so a uniformly scaled placement still reads its rotation.
            double la = ca.Length, lb = cb.Length;
            if (la <= 0 || lb <= 0)
                return 0;
            trace += ca.Dot(cb) / (la * lb);
        }
        return Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1));
    }

    private static Vector3d Column(in Matrix4d m, int c) =>
        m.TransformVector(c == 0 ? Vector3d.UnitX : c == 1 ? Vector3d.UnitY : Vector3d.UnitZ);
}
