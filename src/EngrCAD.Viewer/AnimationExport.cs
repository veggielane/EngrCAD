using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// Animated export: the frame loop over <c>Animation.At(t)</c> and the offscreen
/// renderer. The formats rank deliberately (see design.md): <b>APNG first</b> — nearly
/// free over the dependency-free <see cref="PngWriter"/>, lossless full colour, served
/// as <c>.png</c> because it is one; a <b>PNG frame sequence always available</b> as
/// the zero-risk escape hatch into ffmpeg for MP4/WebM; GIF second (see
/// <see cref="GifWriter"/>'s honest banding note). Every frame is rendered by the same
/// pure evaluation the window's playback scrubs, so an export IS what the viewer
/// showed.
/// </summary>
public static class AnimationExport
{
    /// <summary>
    /// Renders <paramref name="animation"/> over <paramref name="scene"/> to an APNG at
    /// <paramref name="path"/>. Playback time matches the animation's own duration
    /// (per-frame delay = Duration / frames), and the file loops forever.
    /// <para><paramref name="loop"/> picks the sampling: a looping animation samples
    /// t = i/frames (the end frame IS the start frame, so it is not repeated — a
    /// turntable comes around seamlessly); a one-shot samples t = i/(frames−1) so the
    /// final pose is shown exactly.</para>
    /// <para><paramref name="camera"/> applies only when the animation has no camera
    /// track; null auto-frames ONCE over the union of the first and last frames'
    /// bounds (never per frame — a camera chasing the geometry is unusable, the explode
    /// slider's lesson).</para>
    /// </summary>
    public static void RenderApng(
        this Animation animation, Scene scene, string path,
        int frames = 36, int width = 640, int height = 448, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges, bool loop = true,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault)
    {
        var (pixels, _) = RenderFramePixels(
            animation, scene, frames, width, height, camera, style, loop, ambientOcclusion);
        // Delay in milliseconds over a 1000 denominator: exact for any realistic
        // duration, and both halves fit APNG's 16-bit rationals.
        int delayMs = Math.Max(1, (int)Math.Round(animation.Duration / frames * 1000));
        ApngWriter.Write(path, pixels, width, height, delayMs, 1000, plays: 0);
    }

    /// <summary>
    /// Renders the animation as a numbered PNG frame sequence
    /// (<c>frame-0000.png</c> …) into <paramref name="directory"/> — the ffmpeg escape
    /// hatch (<c>ffmpeg -framerate N -i frame-%04d.png out.mp4</c>), which no
    /// dependency-free encoder reaches. Returns the written paths in order.
    /// </summary>
    public static IReadOnlyList<string> RenderFrames(
        this Animation animation, Scene scene, string directory,
        int frames = 36, int width = 640, int height = 448, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges, bool loop = true,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault)
    {
        var (pixels, _) = RenderFramePixels(
            animation, scene, frames, width, height, camera, style, loop, ambientOcclusion);
        Directory.CreateDirectory(directory);
        var paths = new List<string>(pixels.Count);
        for (int i = 0; i < pixels.Count; i++)
        {
            string path = Path.Combine(directory, $"frame-{i:D4}.png");
            PngWriter.Write(path, pixels[i], width, height);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// Renders the animation to an animated GIF. <b>Expect banding on shaded renders</b>:
    /// GIF is 256 colours with no alpha, so the background gradient, smooth shading and
    /// ambient occlusion band visibly, and dithering (deliberately not done) would fight
    /// the clean look — a <see cref="ViewStyle.Wireframe"/> or flat-shaded clip GIFs far
    /// better, and <see cref="RenderApng"/> is the quality route. GIF's one virtue is
    /// that it pastes everywhere.
    /// </summary>
    public static void RenderGif(
        this Animation animation, Scene scene, string path,
        int frames = 36, int width = 640, int height = 448, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges, bool loop = true,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault)
    {
        var (pixels, _) = RenderFramePixels(
            animation, scene, frames, width, height, camera, style, loop, ambientOcclusion);
        // GIF delays are centiseconds; browsers clamp below 2 to a sluggish 10.
        int delayCs = Math.Max(2, (int)Math.Round(animation.Duration / frames * 100));
        GifWriter.Write(path, pixels, width, height, delayCs);
    }

    /// <summary>The shared frame loop: meshes once, resolves one fixed camera when no
    /// camera track exists, evaluates <c>At(t)</c> per frame, renders offscreen.</summary>
    internal static (IReadOnlyList<byte[]> Pixels, CameraState Camera) RenderFramePixels(
        Animation animation, Scene scene, int frames, int width, int height,
        CameraState? camera, ViewStyle style, bool loop, bool ambientOcclusion)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(scene);
        if (frames < 2)
            throw new ArgumentOutOfRangeException(nameof(frames), "An animation export needs at least two frames.");

        scene.PreMesh();
        var defaults = scene.Instances().ToList();

        // One camera for the whole clip unless a track drives it: explicit, or framed
        // over the union of the first and last frames (an explode grows past its
        // assembled bounds, and framing only t = 0 would crop the finale).
        var fixedCamera = camera;
        if (animation.CameraTrack is null && fixedCamera is null)
        {
            var bounds = Aabb.Empty;
            foreach (var instance in animation.At(0).Instances ?? defaults)
                bounds = bounds.Union(instance.Bounds());
            foreach (var instance in animation.At(1).Instances ?? defaults)
                bounds = bounds.Union(instance.Bounds());
            fixedCamera = CameraMath.DefaultCamera(bounds);
        }

        // Evaluate the whole timeline FIRST, then render it in one batch: an animation
        // moves poses, the camera, one deformation scalar and the clip planes — never
        // geometry — so every frame draws the same parts and
        // OffscreenRenderer.RenderSequence can hold one EGL context, one set of linked
        // programs and one set of uploaded buffers for all of them. A deformation track
        // and a section track ride that batching for free precisely because both are
        // shader state: they change what a frame LOOKS like without changing what is in it.
        var timeline =
            new List<(IReadOnlyList<PartInstance> Instances, CameraState Camera, double DeformFactor,
                IReadOnlyList<SectionPlane>? Sections)>(frames);
        CameraState used = fixedCamera ?? CameraMath.DefaultCamera(Aabb.Empty);
        for (int i = 0; i < frames; i++)
        {
            double t = loop ? i / (double)frames : i / (double)(frames - 1);
            var sample = animation.At(t);
            used = sample.Camera ?? fixedCamera
                ?? throw new InvalidOperationException(
                    "No camera: the animation has no camera track and none was supplied.");
            timeline.Add((sample.Instances ?? defaults, used, sample.DeformFactor, sample.Sections));
        }
        var pixels = OffscreenRenderer.RenderSequence(
            timeline, width, height, furniture: true, style, ambientOcclusion: ambientOcclusion);
        return (pixels, used);
    }
}
