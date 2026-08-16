using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;
using GL = Silk.NET.OpenGL.GL;

namespace EngrCAD.Viewer;

/// <summary>
/// Headless rendering: draws parts into an offscreen ANGLE pbuffer (no window, no
/// Avalonia platform, works from tests and CI) and returns raw pixels or writes a
/// PNG. The look matches <see cref="ViewportControl"/> — background gradient, ground
/// grid + axes, directional light with specular, part colors, feature-edge overlay,
/// per-part display modes (wireframe, translucent with the shared back-to-front
/// ordering), the global <see cref="ViewStyle"/>, and axis-aligned section planes
/// including their SDF isoline overlays (parts with an implicit route get
/// iso-distance contours on the cut, via the same <see cref="SectionContourRenderer"/>
/// as the window) — because both passes draw with the same shaders, camera math, mode
/// resolution, and
/// furniture geometry from <c>RenderCore.cs</c> (<see cref="ViewerShaders"/>/
/// <see cref="CameraMath"/>/<see cref="RenderModes"/>/<see cref="RenderGeometry"/>).
/// The only shader feature disabled here is the selection highlight (uHighlight 0 —
/// there is no interactive selection offscreen).
/// </summary>
public static class OffscreenRenderer
{
    private static readonly Lazy<string?> Availability = new(() =>
    {
        // Operational kill switch AND the test seam for the no-GL error path: a
        // GPU-less machine cannot be simulated in-process (the probe is a process-wide
        // Lazy over a real EGL context), so tests — and users with a broken driver —
        // force the unavailable path via the environment.
        if (Environment.GetEnvironmentVariable("ENGRCAD_NO_GL") is "1" or "true")
            return "offscreen GL disabled by ENGRCAD_NO_GL";
        try
        {
            var context = EglContext.TryCreate(4, 4, out var error);
            context?.Dispose();
            return error;
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    });

    /// <summary>Whether an offscreen GL context can be created on this machine.</summary>
    public static bool IsAvailable => Availability.Value is null;

    /// <summary>Why <see cref="IsAvailable"/> is false (null when it is true).</summary>
    public static string? UnavailableReason => Availability.Value;

    /// <summary>
    /// Renders <paramref name="parts"/> and returns RGBA8 pixels, top row first
    /// (width * height * 4 bytes). Parts should be pre-meshed (<c>Scene.PreMesh</c>).
    /// A null <paramref name="camera"/> auto-frames an iso-style view exactly like the
    /// viewer's first visit (yaw 0.7, pitch 0.45, distance from the parts' bounds).
    /// <paramref name="furniture"/> controls the ground grid and axes.
    /// <paramref name="style"/> is the global view style; each part's own
    /// <c>Part.DisplayMode</c> overrides it where explicitly non-default, exactly as in
    /// the window (<see cref="ViewStyle"/> documents the precedence). A non-null
    /// <paramref name="sectionOffset"/> enables the section plane perpendicular to
    /// <paramref name="sectionAxis"/>: geometry beyond the offset is clipped and
    /// exposed interiors shade as flat cut material, matching the viewport's Section
    /// toggle. Throws <see cref="InvalidOperationException"/> when no GL context is
    /// available; check <see cref="IsAvailable"/> first for a graceful skip.
    /// </summary>
    public static byte[] Render(
        IReadOnlyList<Part> parts, int width, int height, CameraState? camera = null, bool furniture = true,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview = null, Matrix4d? previewWorld = null,
        bool fields = true, double deformFactor = 1, ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop) =>
        Render([.. parts.Select(p => new PartInstance(p, p.Transform, p.Name))],
            width, height, camera, furniture, style, sectionAxis, sectionOffset, ambientOcclusion,
            sectionPlanes, sectionCombine, preview, previewWorld, fields, deformFactor, shading, annotationDepth);

    /// <summary>
    /// Renders posed part instances (<c>Tab.Instances()</c> / <c>Scene.AllInstances</c>
    /// — assemblies flattened; instances of the same part share one uploaded mesh) and
    /// returns RGBA8 pixels, top row first. See
    /// <see cref="Render(IReadOnlyList{Part}, int, int, CameraState?, bool, ViewStyle, SectionAxis, double?, bool)"/>.
    /// </summary>
    public static byte[] Render(
        IReadOnlyList<PartInstance> instances, int width, int height,
        CameraState? camera = null, bool furniture = true,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview = null, Matrix4d? previewWorld = null,
        bool fields = true, double deformFactor = 1, ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop,
        (FieldSequenceTrack Track, string FieldName)? fieldStep = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        // Render at 2x and box-downsample: deterministic anti-aliasing that works on
        // every backend (MSAA pbuffers are unreliable under ANGLE/WARP). The projection
        // uses the aspect ratio only, so the camera framing is identical.
        const int supersample = 2;
        using var egl = EglContext.TryCreate(width * supersample, height * supersample, out var error)
            ?? throw new InvalidOperationException($"Offscreen rendering is not available: {error}");
        using var gl = GL.GetApi(new LamdaNativeContext(egl.GetFunction));
        var cache = new PassCache(gl);
        var oversized = Draw(gl, cache, instances, width * supersample, height * supersample, camera, furniture,
            style, sectionAxis, sectionOffset, ambientOcclusion, sectionPlanes, sectionCombine, supersample,
            preview, previewWorld, fields, deformFactor, shading, annotationDepth, fieldStep);
        return Downsample(oversized, width, height, supersample);
    }

    /// <summary>
    /// Renders a SEQUENCE of frames — an animation export — through <b>one</b> EGL
    /// context, one set of linked programs and <b>one</b> set of uploaded per-part
    /// buffers. Only the per-instance matrices and the camera change between frames,
    /// which is the offscreen restatement of the insight that lets the window animate
    /// through <c>SetInstancePoses</c> without touching a GPU buffer.
    /// <para>Every frame must carry the SAME parts (an animation moves poses, never
    /// geometry — <see cref="Animation"/>'s load-bearing rule), so the upload cache is
    /// keyed by <see cref="Part"/> reference and hits from the second frame on. Output
    /// is byte-identical to calling <see cref="Render(IReadOnlyList{PartInstance}, int, int, CameraState?, bool, ViewStyle, SectionAxis, double?, bool, IReadOnlyList{SectionPlane}?, SectionCombine, IReadOnlyList{ValueTuple{Vector3d, Vector3d}}?, Matrix4d?, bool)"/>
    /// once per frame; a test asserts exactly that.</para>
    /// </summary>
    /// <param name="frames">Per frame: the posed instances and the camera to draw them with.</param>
    public static IReadOnlyList<byte[]> RenderSequence(
        IReadOnlyList<(IReadOnlyList<PartInstance> Instances, CameraState Camera, double DeformFactor)> frames,
        int width, int height, bool furniture = true,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        bool fields = true, ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return RenderSequence(
            [.. frames.Select(f =>
                (f.Instances, f.Camera, f.DeformFactor, (IReadOnlyList<SectionPlane>?)null))],
            width, height, furniture, style, sectionAxis, sectionOffset, ambientOcclusion,
            sectionPlanes, sectionCombine, fields, shading, annotationDepth);
    }

    /// <summary>
    /// <see cref="RenderSequence(IReadOnlyList{ValueTuple{IReadOnlyList{PartInstance}, CameraState, double}}, int, int, bool, ViewStyle, SectionAxis, double?, bool, IReadOnlyList{SectionPlane}?, SectionCombine, bool, ShadingStyle, AnnotationDepth)"/>
    /// with PER-FRAME section planes — a frame's own planes (a section track's output) win over
    /// the call-level <paramref name="sectionPlanes"/>; null keeps the call-level ones, so a
    /// sequence with no section track is bit-identical to before. A clip plane is shader state,
    /// so per-frame sections ride the one-context batch exactly as the deformation scalar does:
    /// they change what a frame LOOKS like without changing what is in it.
    /// </summary>
    /// <param name="fieldSteps">Per-frame field-sequence selections, parallel to
    /// <paramref name="frames"/> — a transient playback's step at each instant, applied
    /// through the track's own <c>TryDisplayFor</c> rule exactly as a single still
    /// applies it; a warm cache re-uploads only the colour floats when the step moved.
    /// Null (the default) keeps every frame on the parts' own displays, bit-identical to
    /// before the parameter existed.</param>
    public static IReadOnlyList<byte[]> RenderSequence(
        IReadOnlyList<(IReadOnlyList<PartInstance> Instances, CameraState Camera, double DeformFactor,
            IReadOnlyList<SectionPlane>? Sections)> frames,
        int width, int height, bool furniture = true,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        bool fields = true, ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop,
        IReadOnlyList<(FieldSequenceTrack Track, string FieldName)?>? fieldSteps = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (fieldSteps is not null && fieldSteps.Count != frames.Count)
            throw new ArgumentException(
                $"fieldSteps must be parallel to frames: {fieldSteps.Count} selections for "
                + $"{frames.Count} frames.", nameof(fieldSteps));
        if (frames.Count == 0)
            return [];

        const int supersample = 2;
        using var egl = EglContext.TryCreate(width * supersample, height * supersample, out var error)
            ?? throw new InvalidOperationException($"Offscreen rendering is not available: {error}");
        using var gl = GL.GetApi(new LamdaNativeContext(egl.GetFunction));
        var cache = new PassCache(gl);
        var pixels = new List<byte[]>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            var (instances, camera, deformFactor, sections) = frames[i];
            var oversized = Draw(gl, cache, instances, width * supersample, height * supersample, camera,
                furniture, style, sectionAxis, sectionOffset, ambientOcclusion,
                sections ?? sectionPlanes, sectionCombine,
                supersample, preview: null, previewWorld: null, fields, deformFactor, shading, annotationDepth,
                fieldStep: fieldSteps?[i]);
            pixels.Add(Downsample(oversized, width, height, supersample));
        }
        return pixels;
    }

    /// <summary>Box-filter downsample of RGBA8 pixels by an integer factor.</summary>
    private static byte[] Downsample(byte[] source, int width, int height, int factor)
    {
        if (factor == 1)
            return source;
        int sourceWidth = width * factor;
        var result = new byte[width * height * 4];
        int samples = factor * factor;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < factor; sy++)
                {
                    int row = ((y * factor + sy) * sourceWidth + x * factor) * 4;
                    for (int sx = 0; sx < factor; sx++)
                    {
                        r += source[row];
                        g += source[row + 1];
                        b += source[row + 2];
                        a += source[row + 3];
                        row += 4;
                    }
                }
                int destination = (y * width + x) * 4;
                result[destination] = (byte)(r / samples);
                result[destination + 1] = (byte)(g / samples);
                result[destination + 2] = (byte)(b / samples);
                result[destination + 3] = (byte)(a / samples);
            }
        }
        return result;
    }

    /// <summary>Renders <paramref name="parts"/> to a PNG file. See
    /// <see cref="Render(IReadOnlyList{Part}, int, int, CameraState?, bool, ViewStyle, SectionAxis, double?, bool)"/>.</summary>
    public static void RenderToImage(
        IReadOnlyList<Part> parts, string path, int width = 1280, int height = 800,
        CameraState? camera = null, bool furniture = true,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview = null, Matrix4d? previewWorld = null,
        bool fields = true, double deformFactor = 1, ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop)
    {
        var pixels = Render(parts, width, height, camera, furniture, style, sectionAxis, sectionOffset,
            ambientOcclusion, sectionPlanes, sectionCombine, preview, previewWorld, fields, deformFactor,
            shading, annotationDepth);
        PngWriter.Write(path, pixels, width, height);
    }

    /// <summary>Renders posed part instances to a PNG file. See
    /// <see cref="Render(IReadOnlyList{PartInstance}, int, int, CameraState?, bool, ViewStyle, SectionAxis, double?, bool)"/>.</summary>
    public static void RenderToImage(
        IReadOnlyList<PartInstance> instances, string path, int width = 1280, int height = 800,
        CameraState? camera = null, bool furniture = true,
        ViewStyle style = ViewStyle.ShadedWithEdges,
        SectionAxis sectionAxis = SectionAxis.Z, double? sectionOffset = null,
        bool ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine sectionCombine = SectionCombine.Intersection,
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview = null, Matrix4d? previewWorld = null,
        bool fields = true, double deformFactor = 1, ShadingStyle shading = ShadingStyle.Lit,
        AnnotationDepth annotationDepth = AnnotationDepth.AlwaysOnTop,
        (FieldSequenceTrack Track, string FieldName)? fieldStep = null)
    {
        var pixels = Render(instances, width, height, camera, furniture, style, sectionAxis, sectionOffset,
            ambientOcclusion, sectionPlanes, sectionCombine, preview, previewWorld, fields, deformFactor,
            shading, annotationDepth, fieldStep);
        PngWriter.Write(path, pixels, width, height);
    }

    // ---- the render pass (mirrors ViewportControl.OnOpenGlRender) ----

    /// <summary>One instance's draw data after mode resolution (buffers shared per part).</summary>
    private readonly record struct InstanceDraw(
        EffectiveMode Mode, uint Vao, int IndexCount, uint EdgeVao, int EdgeVertexCount,
        uint WireVao, int WireVertexCount, bool WireFieldColored,
        Matrix4d Model, PartColor Color, Vector3d WorldCenter,
        bool SectionClipped, bool FieldColored, double DeformScale, uint GhostVao, int GhostIndexCount);

    /// <summary>The per-part GL buffers a draw needs, shared by every instance of that
    /// part. (The CPU-side data behind them is the shared <see cref="PartUpload"/>; this
    /// is only what survives once it has been handed to GL.)</summary>
    private readonly record struct PartBuffers(
        uint Vao, int IndexCount, uint EdgeVao, int EdgeVertexCount,
        uint WireVao, int WireVertexCount, bool WireFieldColored,
        bool FieldColored, double DeformScale,
        uint GhostVao, int GhostIndexCount);

    /// <summary>
    /// What survives between frames of one context: the four linked programs and the
    /// per-part uploads. A single <see cref="Render"/> call builds one and throws it
    /// away with the context; <see cref="RenderSequence"/> reuses one across every
    /// frame, which is where an animation export's saving comes from (programs link
    /// once, meshes upload once, and the CPU-side <c>RenderMesh.CreateFlat</c> /
    /// occlusion / feature-edge work behind each upload happens once too).
    /// </summary>
    private sealed class PassCache
    {
        public PassCache(GL gl)
        {
            // The pbuffer context is always GLES3 (ANGLE), hence the ES header.
            string header = ViewerShaders.Header(es: true);
            MeshProgram = ViewerPrograms.LinkProgram(
                gl, header + ViewerShaders.MeshVertex, header + ViewerShaders.MeshFragment, bindAttributes: true);
            LineProgram = ViewerPrograms.LinkProgram(
                gl, header + ViewerShaders.LineVertex, header + ViewerShaders.LineFragment, bindAttributes: true);
            PointProgram = ViewerPrograms.LinkProgram(
                gl, header + ViewerShaders.PointVertex, header + ViewerShaders.PointFragment, bindAttributes: true);
            BackgroundProgram = ViewerPrograms.LinkProgram(
                gl, header + ViewerShaders.BackgroundVertex, header + ViewerShaders.BackgroundFragment,
                bindAttributes: false);
        }

        public uint MeshProgram { get; }
        public uint LineProgram { get; }
        public uint PointProgram { get; }
        public uint BackgroundProgram { get; }

        /// <summary>Uploads keyed by <see cref="Part"/> REFERENCE — the same identity
        /// <c>Scene.AllParts</c> dedupes by, so N instances of one part share one set.</summary>
        public Dictionary<Part, PartBuffers> Uploaded { get; } = [];

        /// <summary>
        /// Transient playback across the batch: per field-coloured part, the slim data a
        /// colours-only re-upload needs — the live colour VBO and the source-index
        /// lookups — captured at upload (the window's <c>_fieldAnimation</c>, one context
        /// over). A cache HIT whose frame selects a different step re-uploads through
        /// this rather than rebuilding anything.
        /// </summary>
        public Dictionary<Part, (uint FieldVbo, int[] VertexLookup, int[] FaceLookup)>
            FieldAnimation { get; } = [];

        /// <summary>The step the cached colour buffers currently show — so a run of
        /// frames holding one step re-uploads nothing (the hold-last common case).</summary>
        public (FieldSequenceTrack Track, string FieldName)? AppliedFieldStep { get; set; }
    }

    private static unsafe byte[] Draw(
        GL gl, PassCache cache, IReadOnlyList<PartInstance> instances, int width, int height,
        CameraState? camera, bool furniture,
        ViewStyle style, SectionAxis sectionAxis, double? sectionOffset, bool ambientOcclusion,
        IReadOnlyList<SectionPlane>? sectionPlanes, SectionCombine sectionCombine, int supersample,
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview, Matrix4d? previewWorld, bool fields,
        double deformFactor, ShadingStyle shading, AnnotationDepth annotationDepth,
        (FieldSequenceTrack Track, string FieldName)? fieldStep = null)
    {
        uint meshProgram = cache.MeshProgram;
        uint lineProgram = cache.LineProgram;
        uint pointProgram = cache.PointProgram;
        uint bgProgram = cache.BackgroundProgram;

        var bounds = Aabb.Empty;
        foreach (var instance in instances)
            bounds = bounds.Union(instance.Bounds());
        var cam = camera ?? DefaultCamera(bounds);

        // One plane set for the whole pass: an explicit list wins, otherwise the
        // axis+offset pair builds the single-plane cut (null offset = no section).
        IReadOnlyList<SectionPlane> planes = sectionPlanes is { Count: > 0 } explicitPlanes
            ? [.. explicitPlanes.Take(ViewerShaders.MaxSectionPlanes)]
            : sectionOffset.HasValue ? [SectionPlane.On(sectionAxis, sectionOffset.Value)] : [];
        bool section = planes.Count > 0;

        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(0.11f, 0.12f, 0.14f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        // Meshes without a baked-occlusion buffer read this constant (the GL default
        // would be 0 = fully occluded = black parts); the same rule for the
        // field-colour attribute, whose strength uniform is 0 for such a part anyway.
        RenderUploads.SetDefaultOcclusion(gl);
        RenderUploads.SetDefaultFieldColor(gl);
        RenderUploads.SetDefaultDeformation(gl);

        // Background gradient: vertexless fullscreen triangle, no depth.
        uint bgVao = gl.GenVertexArray();
        gl.Disable(EnableCap.DepthTest);
        gl.UseProgram(bgProgram);
        gl.BindVertexArray(bgVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.Enable(EnableCap.DepthTest);

        var eye = CameraMath.Eye(cam.Yaw, cam.Pitch, cam.Distance, cam.Target);
        var view = CameraMath.LookAt(eye, cam.Target, Vector3d.UnitZ);
        var (nearPlane, farPlane) = CameraMath.FrustumPlanes(cam.Distance, bounds);
        var proj = CameraMath.Perspective(Math.PI / 4, (double)width / height, nearPlane, farPlane);

        Span<float> matrix = stackalloc float[16];

        gl.UseProgram(lineProgram);
        int uLineModel = gl.GetUniformLocation(lineProgram, "uModel");
        int uLineView = gl.GetUniformLocation(lineProgram, "uView");
        int uLineProj = gl.GetUniformLocation(lineProgram, "uProj");
        int uLineColor = gl.GetUniformLocation(lineProgram, "uColor");
        int uLineFieldColor = gl.GetUniformLocation(lineProgram, "uFieldColor");
        int uLineSectionEnabled = gl.GetUniformLocation(lineProgram, "uSectionEnabled");
        var lineSection = new SectionUniforms(gl, lineProgram);
        CameraMath.WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(uLineView, 1, false, matrix);
        CameraMath.WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(uLineProj, 1, false, matrix);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uLineModel, 1, false, matrix);
        lineSection.Write(gl, planes, sectionCombine);
        lineSection.SetEnabled(gl, false);   // grid/axes are scene furniture — never clipped

        if (furniture)
        {
            var (gridVertices, axesVertices) = RenderGeometry.BuildGridAndAxes(bounds);
            var (gridVao, _) = RenderUploads.UploadLines(gl, gridVertices);
            var (axesVao, _) = RenderUploads.UploadLines(gl, axesVertices);

            gl.Uniform3(uLineColor, 0.24f, 0.26f, 0.29f);
            gl.BindVertexArray(gridVao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(gridVertices.Length / 3));

            gl.BindVertexArray(axesVao);
            gl.Uniform3(uLineColor, 0.75f, 0.30f, 0.30f);
            gl.DrawArrays(PrimitiveType.Lines, 0, 2);           // +X
            gl.Uniform3(uLineColor, 0.33f, 0.66f, 0.33f);
            gl.DrawArrays(PrimitiveType.Lines, 2, 2);           // +Y
            gl.Uniform3(uLineColor, 0.35f, 0.48f, 0.85f);
            gl.DrawArrays(PrimitiveType.Lines, 4, 2);           // +Z
        }

        // One upload per distinct part; instances draw the shared buffers with their
        // own world matrices (Part has reference identity, so the dictionary dedupes).
        // The effective mode is per part (global style x Part.DisplayMode, resolved by
        // the shared RenderModes.Resolve), so only the buffers that mode needs are
        // uploaded: mesh VAO for fills/points/translucency, feature edges for the
        // shaded-with-edges look and translucent silhouettes, wire edges for wireframe.
        var uploaded = cache.Uploaded;
        // A field-sequence frame against a WARM cache: when the selection moved since the
        // colours were last uploaded, re-upload just the colour floats into each
        // participating part's retained VBO (the attribute pointer references the buffer
        // OBJECT, so no VAO is touched) — the batched twin of the window's
        // ApplyPendingFieldSelection, and the whole per-frame cost the measurement put at
        // 0.042/0.68 ms. Parts this frame is about to upload fresh take the same step in
        // the miss path below, so both roads end at one configuration.
        if (fieldStep is { } selection
            && (cache.AppliedFieldStep is not { } applied
                || !ReferenceEquals(applied.Track, selection.Track)
                || applied.FieldName != selection.FieldName))
        {
            foreach (var (cachedPart, animation) in cache.FieldAnimation)
            {
                if (!selection.Track.TryDisplayFor(cachedPart, selection.FieldName, out var stepDisplay))
                    continue;
                var lookup = stepDisplay.Field.Association == FieldAssociation.Cell
                    ? animation.FaceLookup
                    : animation.VertexLookup;
                var stepColors = FieldRendering.Colors(
                    stepDisplay.Field, stepDisplay.Range, stepDisplay.ColorMap, lookup,
                    stepDisplay.LogScale);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, animation.FieldVbo);
                gl.BufferData<float>(BufferTargetARB.ArrayBuffer, stepColors, BufferUsageARB.StaticDraw);
            }
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }
        if (fieldStep is not null)
            cache.AppliedFieldStep = fieldStep;
        var draws = new List<InstanceDraw>(instances.Count);
        foreach (var instance in instances)
        {
            var part = instance.Part;
            // EffectiveDisplayMode, not DisplayMode: a Ghost part renders translucent
            // in every front end (the debug-modifier contract).
            var mode = RenderModes.Resolve(style, part.EffectiveDisplayMode);
            if (!uploaded.TryGetValue(part, out var shared))
            {
                // The CPU half is the shared PartUploads.Build — the SAME call the window
                // and the browser make, so all three build identical render meshes, colour
                // and displacement floats, and edge segments. What is one-shot-specific is
                // the POLICY: this pass resolved the effective mode before uploading and
                // has no dropdown to change its mind, so it asks only for the pieces that
                // mode draws, and never for a pick BVH.
                bool shaded = mode != EffectiveMode.Wireframe;
                var upload = PartUploads.Build(part, new PartUploadRequest
                {
                    Fields = fields,
                    FeatureEdges = mode is EffectiveMode.ShadedWithEdges or EffectiveMode.Translucent,
                    WireEdges = mode == EffectiveMode.Wireframe,
                    Pick = false,
                    // Baked inline (unlike the window's never-bake cache read): a one-shot
                    // render must be deterministic. Cached per mesh and shared with the
                    // window pass, so both shade from identical floats. Skipped entirely
                    // for wireframe, which uploads no fill to shade.
                    Occlusion = ambientOcclusion && shaded
                        ? static (m, r) => Viewer.AmbientOcclusion.For(m, r)
                        : null,
                });
                var render = upload.Render;
                var field = upload.Field;
                // Transient playback: a still of a FieldSequenceTrack step swaps the
                // participating part's display for the step's field over the run's ONE
                // range (the track's own TryDisplayFor rule), rebuilding just the
                // colour floats — the deformation buffers and everything else ride
                // through untouched.
                if (fieldStep is { } step && field is { } data
                    && step.Track.TryDisplayFor(part, step.FieldName, out var swapped))
                {
                    field = data with
                    {
                        Display = swapped,
                        Colors = FieldRendering.Colors(
                            swapped.Field, swapped.Range, swapped.ColorMap, render,
                            swapped.LogScale),
                    };
                }

                uint vao = 0;
                int indexCount = 0;
                uint ghostVao = 0;
                int ghostIndexCount = 0;
                if (shaded)
                {
                    uint fieldVbo;
                    (vao, _, _, _, fieldVbo, _) = RenderUploads.UploadMesh(
                        gl, render, upload.Occlusion, field?.Colors, field?.Deformation);
                    indexCount = upload.IndexCount;
                    if (field is not null && fieldVbo != 0)
                        cache.FieldAnimation[part] =
                            (fieldVbo, render.SourceVertices, render.SourceFaces);
                    if (upload.ShowGhost)
                    {
                        (ghostVao, _, _, _, _, _) = RenderUploads.UploadMesh(gl, render);
                        ghostIndexCount = upload.IndexCount;
                    }
                }
                uint edgeVao = 0;
                int edgeVertexCount = 0;
                if (upload.FeatureEdges.Count > 0)
                {
                    (edgeVao, _, _, _) = RenderUploads.UploadLines(
                        gl, RenderGeometry.SegmentVertices(upload.FeatureEdges), null,
                        upload.FeatureEdgeDeformation);
                    edgeVertexCount = upload.FeatureEdgeVertexCount;
                }
                uint wireVao = 0;
                int wireVertexCount = 0;
                bool wireFieldColored = false;
                if (upload.WireEdges.Count > 0)
                {
                    (wireVao, _, _, _) = RenderUploads.UploadLines(
                        gl, RenderGeometry.SegmentVertices(upload.WireEdges), upload.WireColors,
                        upload.WireDeformation);
                    wireVertexCount = upload.WireEdgeVertexCount;
                    wireFieldColored = upload.WireColors is not null;
                }
                shared = new PartBuffers(vao, indexCount, edgeVao, edgeVertexCount,
                    wireVao, wireVertexCount, wireFieldColored,
                    upload.FieldColored, upload.DeformScale, ghostVao, ghostIndexCount);
                uploaded[part] = shared;
            }

            var worldBounds = instance.Bounds();
            draws.Add(new InstanceDraw(
                mode, shared.Vao, shared.IndexCount, shared.EdgeVao, shared.EdgeVertexCount,
                shared.WireVao, shared.WireVertexCount, shared.WireFieldColored,
                instance.World, part.Color ?? Palette.Steel,
                worldBounds.IsEmpty ? Vector3d.Zero : worldBounds.Center,
                section && part.ClippedBySection,
                shared.FieldColored, shared.DeformScale, shared.GhostVao, shared.GhostIndexCount));
        }

        // Shaded fills, pushed back slightly so the edge overlay wins the depth test.
        gl.UseProgram(meshProgram);
        int uModel = gl.GetUniformLocation(meshProgram, "uModel");
        int uView = gl.GetUniformLocation(meshProgram, "uView");
        int uProj = gl.GetUniformLocation(meshProgram, "uProj");
        int uColor = gl.GetUniformLocation(meshProgram, "uColor");
        int uLightDir = gl.GetUniformLocation(meshProgram, "uLightDir");
        int uEyePos = gl.GetUniformLocation(meshProgram, "uEyePos");
        int uAlpha = gl.GetUniformLocation(meshProgram, "uAlpha");
        CameraMath.WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(uView, 1, false, matrix);
        CameraMath.WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(uProj, 1, false, matrix);
        var lightDir = new Vector3d(-0.5, -0.7, -0.9).Normalized();
        gl.Uniform3(uLightDir, (float)lightDir.X, (float)lightDir.Y, (float)lightDir.Z);
        gl.Uniform3(uEyePos, (float)eye.X, (float)eye.Y, (float)eye.Z);
        gl.Uniform1(gl.GetUniformLocation(meshProgram, "uHighlight"), 0f);  // no selection offscreen
        var meshSection = new SectionUniforms(gl, meshProgram);
        meshSection.Write(gl, planes, sectionCombine);
        gl.Uniform1(gl.GetUniformLocation(meshProgram, "uAmbientOcclusion"),
            ambientOcclusion ? Viewer.AmbientOcclusion.Strength : 0f);
        // Frame-constant shading selector (analytic matcap or the standard lighting),
        // an INT uniform — the same value the window writes, so the two passes agree.
        gl.Uniform1(gl.GetUniformLocation(meshProgram, "uMatcap"), (int)shading);
        int uFieldColor = gl.GetUniformLocation(meshProgram, "uFieldColor");
        int uDeformScale = gl.GetUniformLocation(meshProgram, "uDeformScale");
        gl.Uniform1(uAlpha, 1f);

        // Section mode relies on face culling staying OFF (nothing here enables
        // CullFace): exposed interiors are backfaces, shaded as cut material via
        // gl_FrontFacing in the shared fragment shader.
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1f, 1f);
        foreach (var d in draws)
        {
            if (d.Mode is not (EffectiveMode.Shaded or EffectiveMode.ShadedWithEdges))
                continue;
            // Per-PART section switch (Part.ClippedBySection): a fastener or rib draws
            // whole inside a cutaway, the drafting convention. Same rule, same place in
            // the pass as the window's SectionFor.
            meshSection.SetEnabled(gl, d.SectionClipped);
            CameraMath.WriteColumnMajor(d.Model, matrix);
            gl.UniformMatrix4(uModel, 1, false, matrix);
            gl.Uniform3(uColor, d.Color.R, d.Color.G, d.Color.B);
            gl.Uniform1(uFieldColor, d.FieldColored ? FieldRendering.Strength : 0f);
            gl.Uniform1(uDeformScale, (float)(d.DeformScale * deformFactor));
            gl.BindVertexArray(d.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)d.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        // Line overlay: feature edges for shaded-with-edges parts, full wireframe for
        // wireframe parts. Model lines are section-clipped consistently with fills.
        gl.UseProgram(lineProgram);
        int uLineDeformScale = gl.GetUniformLocation(lineProgram, "uDeformScale");
        foreach (var d in draws)
        {
            lineSection.SetEnabled(gl, d.SectionClipped);   // model lines clip with their fill
            switch (d.Mode)
            {
                case EffectiveMode.ShadedWithEdges when d.EdgeVertexCount > 0:
                    CameraMath.WriteColumnMajor(d.Model, matrix);
                    gl.UniformMatrix4(uLineModel, 1, false, matrix);
                    gl.Uniform3(uLineColor, 0.09f, 0.10f, 0.11f);
                    // A displaced part's edges follow the fills (attribute-absent parts
                    // are immune: a disabled attribute 4 reads (0,0,0)).
                    gl.Uniform1(uLineDeformScale, (float)(d.DeformScale * deformFactor));
                    gl.BindVertexArray(d.EdgeVao);
                    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)d.EdgeVertexCount);
                    break;
                case EffectiveMode.Wireframe when d.WireVertexCount > 0:
                    CameraMath.WriteColumnMajor(d.Model, matrix);
                    gl.UniformMatrix4(uLineModel, 1, false, matrix);
                    gl.Uniform3(uLineColor, d.Color.R, d.Color.G, d.Color.B);
                    gl.Uniform1(uLineDeformScale, (float)(d.DeformScale * deformFactor));
                    // A field-coloured wireframe draws its result; reset before the
                    // next line consumer (the window pass's rule).
                    if (d.WireFieldColored)
                        gl.Uniform1(uLineFieldColor, FieldRendering.Strength);
                    gl.BindVertexArray(d.WireVao);
                    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)d.WireVertexCount);
                    if (d.WireFieldColored)
                        gl.Uniform1(uLineFieldColor, 0f);
                    break;
            }
        }

        // Points pass: indexed point sprites over the mesh buffers (a flat RenderMesh
        // references each vertex exactly once). The supersampled framebuffer needs the
        // point size scaled to keep the final on-image dot size constant.
        if (draws.Exists(d => d.Mode == EffectiveMode.Points))
        {
            gl.UseProgram(pointProgram);
            int uPointModel = gl.GetUniformLocation(pointProgram, "uModel");
            int uPointColor = gl.GetUniformLocation(pointProgram, "uColor");
            int uPointDeformScale = gl.GetUniformLocation(pointProgram, "uDeformScale");
            CameraMath.WriteColumnMajor(view, matrix);
            gl.UniformMatrix4(gl.GetUniformLocation(pointProgram, "uView"), 1, false, matrix);
            CameraMath.WriteColumnMajor(proj, matrix);
            gl.UniformMatrix4(gl.GetUniformLocation(pointProgram, "uProj"), 1, false, matrix);
            gl.Uniform1(gl.GetUniformLocation(pointProgram, "uPointSize"), 4f * supersample);
            var pointSection = new SectionUniforms(gl, pointProgram);
            pointSection.Write(gl, planes, sectionCombine);
            foreach (var d in draws)
            {
                if (d.Mode != EffectiveMode.Points)
                    continue;
                pointSection.SetEnabled(gl, d.SectionClipped);
                CameraMath.WriteColumnMajor(d.Model, matrix);
                gl.UniformMatrix4(uPointModel, 1, false, matrix);
                gl.Uniform3(uPointColor, d.Color.R, d.Color.G, d.Color.B);
                gl.Uniform1(gl.GetUniformLocation(pointProgram, "uFieldColor"),
                    d.FieldColored ? FieldRendering.Strength : 0f);
                gl.Uniform1(uPointDeformScale, (float)(d.DeformScale * deformFactor));
                gl.BindVertexArray(d.Vao);
                gl.DrawElements(PrimitiveType.Points, (uint)d.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
        }

        // Translucent pass, after everything opaque: blended fills back-to-front by
        // part center (the ordering rule shared with the window pass via
        // RenderModes.SortBackToFront), depth writes off, then their feature edges
        // opaque on top for a readable silhouette.
        var translucentOrder = new int[draws.Count];
        var translucentDepth = new double[draws.Count];
        int translucentCount = 0;
        for (int i = 0; i < draws.Count; i++)
        {
            if (draws[i].Mode == EffectiveMode.Translucent)
            {
                translucentOrder[translucentCount] = i;
                translucentDepth[translucentCount] = (draws[i].WorldCenter - eye).LengthSquared;
                translucentCount++;
            }
        }
        if (translucentCount > 0)
        {
            RenderModes.SortBackToFront(translucentOrder, translucentDepth, translucentCount);

            gl.UseProgram(meshProgram);
            gl.Uniform1(uAlpha, 0.4f);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(false);
            for (int k = 0; k < translucentCount; k++)
            {
                var d = draws[translucentOrder[k]];
                meshSection.SetEnabled(gl, d.SectionClipped);
                CameraMath.WriteColumnMajor(d.Model, matrix);
                gl.UniformMatrix4(uModel, 1, false, matrix);
                gl.Uniform3(uColor, d.Color.R, d.Color.G, d.Color.B);
                gl.Uniform1(uFieldColor, d.FieldColored ? FieldRendering.Strength : 0f);
                gl.Uniform1(uDeformScale, (float)(d.DeformScale * deformFactor));
                gl.BindVertexArray(d.Vao);
                gl.DrawElements(PrimitiveType.Triangles, (uint)d.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);

            gl.UseProgram(lineProgram);
            gl.Uniform3(uLineColor, 0.09f, 0.10f, 0.11f);
            for (int k = 0; k < translucentCount; k++)
            {
                var d = draws[translucentOrder[k]];
                if (d.EdgeVertexCount == 0)
                    continue;
                lineSection.SetEnabled(gl, d.SectionClipped);
                CameraMath.WriteColumnMajor(d.Model, matrix);
                gl.UniformMatrix4(uLineModel, 1, false, matrix);
                gl.Uniform1(uLineDeformScale, (float)(d.DeformScale * deformFactor));
                gl.BindVertexArray(d.EdgeVao);
                gl.DrawArrays(PrimitiveType.Lines, 0, (uint)d.EdgeVertexCount);
            }
        }
        // Undeformed ghosts behind the deformed shapes, in the window's pass position
        // and through the same blend/depth-mask machinery.
        if (draws.Exists(d => d.GhostVao != 0))
        {
            gl.UseProgram(meshProgram);
            gl.Uniform1(uFieldColor, 0f);
            // The ghost geometry carries no displacement buffer; stated for the same
            // reason the window states it.
            gl.Uniform1(uDeformScale, 0f);
            gl.Uniform1(uAlpha, FieldRendering.GhostAlpha);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(false);
            foreach (var d in draws)
            {
                if (d.GhostVao == 0)
                    continue;
                meshSection.SetEnabled(gl, d.SectionClipped);
                CameraMath.WriteColumnMajor(d.Model, matrix);
                gl.UniformMatrix4(uModel, 1, false, matrix);
                gl.Uniform3(uColor, d.Color.R, d.Color.G, d.Color.B);
                gl.BindVertexArray(d.GhostVao);
                gl.DrawElements(
                    PrimitiveType.Triangles, (uint)d.GhostIndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
            gl.Uniform1(uAlpha, 1f);
        }

        // Section-plane SDF isolines, drawn after the translucent pass so they read
        // as an overlay on the cut — the same pass order as the window. Offscreen is
        // one-shot: a fresh SectionContourRenderer builds the geometry once, the
        // window path's axis/offset staleness caching never comes into play, and its
        // GL buffers die with the pbuffer context (nothing here is individually
        // deleted, see the note at the end of this method).
        if (section)
        {
            var allVisible = new bool[instances.Count];
            Array.Fill(allVisible, true);
            gl.UseProgram(lineProgram);
            lineSection.SetEnabled(gl, true);
            new SectionContourRenderer().Draw(
                gl, instances, allVisible, planes, sectionCombine,
                lineProgram, uLineModel, uLineColor, lineSection, matrix, report: static _ => { });
        }

        // Annotations (PMI) draw after the isolines (annotations are chrome-like
        // documentation content over the model overlay): unlike the interactive view
        // cube, dimensions/notes ARE documentation, so the headless pass draws them
        // (docs renders of dimensioned parts carry their dimensions). Geometry
        // building is shared with the window via AnnotationLayer/AnnotationGeometry;
        // the offscreen projection is always perspective and the supersample factor
        // is the pixel scale so text keeps its on-image size.
        AnnotationLayer.DrawOffscreen(gl, instances,
            AnnotationCamera.From(cam, orthographic: false, height, supersample),
            lineProgram, uLineModel, uLineColor, uLineSectionEnabled, matrix, annotationDepth);

        // Construction-tree preview, in the same pass position as the window and through
        // the SAME PreviewLayer — the layer owns the colour, the depth-off rule and the
        // never-section-clipped rule, so headless previews cannot drift from the
        // viewport's. One-shot: the layer is created, uploaded and drawn here, and its
        // buffers die with the pbuffer context like everything else in this method.
        if (preview is { Count: > 0 })
        {
            var layer = new PreviewLayer();
            layer.Set(preview, previewWorld ?? Matrix4d.Identity);
            layer.Draw(gl, lineProgram, uLineModel, uLineColor, uLineSectionEnabled, matrix);
        }

        // The field legend, unlike the view cube, IS drawn headlessly: it is
        // documentation (the same argument that puts dimensions in a docs render), and a
        // colour plot without its scale is a picture of nothing in particular. The
        // supersample factor doubles as the pixel scale so the widget keeps its
        // on-image size, exactly as the annotation text does.
        if (fields)
        {
            // At the EFFECTIVE exaggeration: the legend's title states the factor, so
            // an animated frame must say the number it was drawn at (the window's
            // ActiveFieldDisplays applies the same multiply). Factor 1 leaves it exact.
            var displays = draws.Exists(d => d.FieldColored)
                ? FieldDisplays(instances, fieldStep) : [];
            for (int i = 0; i < displays.Count; i++)
                displays[i] = FieldRendering.AtFactor(displays[i], deformFactor)!.Value;
            new FieldLegendLayer().Draw(gl, displays, width, height, supersample,
                new LineProgramHandles(
                    lineProgram, uLineModel, uLineView, uLineProj, uLineColor, uLineSectionEnabled));
        }

        gl.BindVertexArray(0);
        gl.Finish();

        // Read back and flip: glReadPixels returns the bottom row first.
        var bottomUp = new byte[width * height * 4];
        gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte,
            new Span<byte>(bottomUp));
        var topDown = new byte[bottomUp.Length];
        int stride = width * 4;
        for (int row = 0; row < height; row++)
            System.Buffer.BlockCopy(bottomUp, (height - 1 - row) * stride, topDown, row * stride, stride);
        return topDown;
        // GL resources are not individually deleted: the whole context (and everything
        // in it) is destroyed by the EglContext dispose in Render.
    }

    /// <summary>The viewer's first-visit framing — one source of truth in
    /// <see cref="CameraMath.DefaultCamera"/> (turntable tracks base on it too).</summary>
    private static CameraState DefaultCamera(in Aabb bounds) => CameraMath.DefaultCamera(bounds);

    /// <summary>Every DISTINCT resolvable display, in instance order — one legend
    /// each, matching the window's <c>ViewportControl.ActiveFieldDisplays</c>: a
    /// legend is a single scale, so several parts on different scales get a STACK of
    /// bars rather than one bar that lies.</summary>
    private static List<ResolvedFieldDisplay> FieldDisplays(
        IReadOnlyList<PartInstance> instances,
        (FieldSequenceTrack Track, string FieldName)? fieldStep = null)
    {
        var displays = new List<ResolvedFieldDisplay>();
        foreach (var instance in instances)
        {
            // A playback step's legend shows the STEP's field over the run's one range
            // (the same TryDisplayFor rule the fills applied), so the bar cannot say
            // one thing while the colours say another.
            ResolvedFieldDisplay resolved;
            if (fieldStep is { } step
                && step.Track.TryDisplayFor(instance.Part, step.FieldName, out var swapped))
                resolved = swapped;
            else if (!instance.Part.TryResolveFieldDisplay(out resolved, out _))
                continue;
            if (!displays.Contains(resolved))
                displays.Add(resolved);
        }
        return displays;
    }
}
