using EngrCAD.Core;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

/// <summary>
/// Baking a <see cref="TimeVaryingModel"/> — a model whose GEOMETRY is a function of
/// time (OpenSCAD's <c>$t</c>) — into frames: APNG, a PNG sequence, or GIF, the same
/// three formats and the same ranking <see cref="AnimationExport"/> uses.
/// <para><b>It is an OFFLINE bake and that is the design, not a limitation waiting to be
/// lifted.</b> An <see cref="Animation"/> exports fast because it never touches geometry:
/// every frame draws the same parts, so one context, one set of programs and one set of
/// uploads serve the clip. A morphing model has no such property — each frame is a fresh
/// <c>Scene.PreMesh</c>, i.e. a full lower + tessellate — so a clip that plays for one
/// second costs whatever N of those cost. Measured (win-x64) on the docs fixture — a
/// hoisted plate carrying a twisted column — at <b>8.5 ms a frame of geometry uncached
/// and 4.2 ms cached</b>, and a whole 24-frame bake at 480x360 at <b>1.9 s uncached
/// against 1.2 s cached</b>: the cache halves the geometry, and at that size the render is
/// the larger share of what is left. One instant of a B-Rep model (a boolean bore, a
/// whole-solid round) measures 20-45 ms of geometry alone, which is why the interactive
/// transport refuses to scrub one (see <see cref="TimeVaryingModel.At"/> for the live
/// recipe that does work).</para>
/// <para>What makes it affordable is the model's own cache — an unchanged sub-graph is
/// the same object across frames, so its mesh and lowerings are computed once — and it
/// pays twice: the same reuse hands <see cref="OffscreenRenderer.RenderSequence"/> the
/// same <c>Part</c> object, so its per-part GPU upload is shared too. The report comes
/// back on <see cref="BakedModelAnimation.Cache"/> rather than being logged, because a
/// hit rate is the number that says whether a factory hoisted what it should have.</para>
/// </summary>
public static class ModelAnimation
{
    /// <summary>
    /// Bakes <paramref name="model"/> over <paramref name="frames"/> instants and returns
    /// the pixels — the shared core of every writer here, and the entry point a test uses
    /// to compare frames without touching disk.
    /// <para>Sampling matches <see cref="AnimationExport"/>: a looping bake takes
    /// t = i/frames (the end frame IS the start frame, so it is not repeated), a one-shot
    /// t = i/(frames−1) so the final instant is shown exactly.</para>
    /// <para>The camera is resolved ONCE for the whole clip — explicit, else framed over
    /// the union of EVERY frame's bounds. Framing per frame would make a growing model
    /// pulse; framing only the ends (what a pose animation does, correctly, because an
    /// explode's extremes bracket it) is not enough here, because a morphing model can be
    /// widest in the middle. Every frame is built anyway, so the union costs nothing.</para>
    /// </summary>
    public static BakedModelAnimation Bake(
        TimeVaryingModel model, int frames = 24, int width = 640, int height = 448,
        CameraState? camera = null, ViewStyle style = ViewStyle.ShadedWithEdges, bool loop = false,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (frames < 2)
            throw new ArgumentOutOfRangeException(nameof(frames),
                "A model bake needs at least two frames.");

        // Every scene is built and PREPARED first: the union framing needs all of them,
        // and RenderSequence batches them through one context. That is also the memory
        // cost, stated: the clip's meshes are alive at once (a streaming bake that freed
        // each frame after drawing it would give up both the union camera and the batch).
        var timeline =
            new List<(IReadOnlyList<PartInstance> Instances, CameraState Camera, double DeformFactor,
                IReadOnlyList<SectionPlane>? Sections)>(frames);
        var perFrame = new List<IReadOnlyList<PartInstance>>(frames);
        var bounds = Aabb.Empty;
        for (int i = 0; i < frames; i++)
        {
            double t = loop ? i / (double)frames : i / (double)(frames - 1);
            var scene = model.At(t);
            // Debug modifiers apply per frame exactly as they do to a still: a Hidden part
            // must not even influence the framing.
            var instances = DebugFilter.Shown([.. scene.Instances()]);
            perFrame.Add(instances);
            foreach (var instance in instances)
                bounds = bounds.Union(instance.Bounds());
        }

        var resolved = camera ?? CameraMath.DefaultCamera(bounds);
        foreach (var instances in perFrame)
            timeline.Add((instances, resolved, 1.0, null));

        var pixels = OffscreenRenderer.RenderSequence(
            timeline, width, height, furniture: true, style, ambientOcclusion: ambientOcclusion,
            shading: shading, annotationDepth: annotationDepth,
            // One box for the whole clip: the grid's 1-2-5 spacing and the frustum's
            // near/far planes are read off it, and letting them follow a model that grows
            // makes the grid jump and the depth range shimmer between frames.
            sceneBounds: bounds);
        return new BakedModelAnimation(pixels, resolved, width, height, model.Cache);
    }

    /// <summary>
    /// Bakes the model to an APNG at <paramref name="path"/> — the quality route, served
    /// as <c>.png</c> because an APNG is one (see <see cref="AnimationExport.RenderApng"/>
    /// for the format ranking, which is unchanged here).
    /// </summary>
    /// <param name="durationSeconds">Playback length; the per-frame delay is this over the
    /// frame count. It is a PLAYBACK property only — a bake takes as long as it takes.</param>
    public static BakedModelAnimation RenderApng(
        this TimeVaryingModel model, string path, int frames = 24, double durationSeconds = 4,
        int width = 640, int height = 448, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges, bool loop = false,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop)
    {
        if (!(durationSeconds > 0) || !double.IsFinite(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds),
                "A clip needs a positive finite duration.");
        var baked = Bake(model, frames, width, height, camera, style, loop,
            ambientOcclusion, shading, annotationDepth);
        int delayMs = Math.Max(1, (int)Math.Round(durationSeconds / frames * 1000));
        ApngWriter.Write(path, baked.Frames, width, height, delayMs, 1000, plays: 0);
        return baked;
    }

    /// <summary>Bakes the model as a numbered PNG frame sequence
    /// (<c>frame-0000.png</c> …) into <paramref name="directory"/> — the ffmpeg escape
    /// hatch, exactly as <see cref="AnimationExport.RenderFrames"/> provides it.</summary>
    public static BakedModelAnimation RenderFrames(
        this TimeVaryingModel model, string directory, int frames = 24,
        int width = 640, int height = 448, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges, bool loop = false,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop)
    {
        var baked = Bake(model, frames, width, height, camera, style, loop,
            ambientOcclusion, shading, annotationDepth);
        Directory.CreateDirectory(directory);
        for (int i = 0; i < baked.Frames.Count; i++)
            PngWriter.Write(Path.Combine(directory, $"frame-{i:D4}.png"), baked.Frames[i], width, height);
        return baked;
    }

    /// <summary>Bakes the model to an animated GIF. <b>Expect banding on shaded
    /// renders</b> — the honest note <see cref="AnimationExport.RenderGif"/> carries applies
    /// verbatim: GIF is 256 colours with no alpha, so use it where it pastes and
    /// <see cref="RenderApng"/> where it must look right.</summary>
    public static BakedModelAnimation RenderGif(
        this TimeVaryingModel model, string path, int frames = 24, double durationSeconds = 4,
        int width = 640, int height = 448, CameraState? camera = null,
        ViewStyle style = ViewStyle.ShadedWithEdges, bool loop = false,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop)
    {
        if (!(durationSeconds > 0) || !double.IsFinite(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds),
                "A clip needs a positive finite duration.");
        var baked = Bake(model, frames, width, height, camera, style, loop,
            ambientOcclusion, shading, annotationDepth);
        // GIF delays are centiseconds; browsers clamp below 2 to a sluggish 10.
        int delayCs = Math.Max(2, (int)Math.Round(durationSeconds / frames * 100));
        GifWriter.Write(path, baked.Frames, width, height, delayCs);
        return baked;
    }
}

/// <summary>
/// A baked <see cref="TimeVaryingModel"/>: the frames as RGBA8 pixel arrays, the one
/// camera every frame was drawn with, and what the model's geometry cache did over the
/// clip. Returned rather than only written, so a test can compare frames without a file
/// and a host can report the hit rate.
/// </summary>
public sealed record BakedModelAnimation(
    IReadOnlyList<byte[]> Frames, CameraState Camera, int Width, int Height, ModelCacheReport Cache);
