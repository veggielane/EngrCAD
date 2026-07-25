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
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview = null, Matrix4d? previewWorld = null) =>
        Render([.. parts.Select(p => new PartInstance(p, p.Transform, p.Name))],
            width, height, camera, furniture, style, sectionAxis, sectionOffset, ambientOcclusion,
            sectionPlanes, sectionCombine, preview, previewWorld);

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
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview = null, Matrix4d? previewWorld = null)
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
        var oversized = Draw(gl, instances, width * supersample, height * supersample, camera, furniture,
            style, sectionAxis, sectionOffset, ambientOcclusion, sectionPlanes, sectionCombine, supersample,
            preview, previewWorld);
        return Downsample(oversized, width, height, supersample);
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
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview = null, Matrix4d? previewWorld = null)
    {
        var pixels = Render(parts, width, height, camera, furniture, style, sectionAxis, sectionOffset,
            ambientOcclusion, sectionPlanes, sectionCombine, preview, previewWorld);
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
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview = null, Matrix4d? previewWorld = null)
    {
        var pixels = Render(instances, width, height, camera, furniture, style, sectionAxis, sectionOffset,
            ambientOcclusion, sectionPlanes, sectionCombine, preview, previewWorld);
        PngWriter.Write(path, pixels, width, height);
    }

    // ---- the render pass (mirrors ViewportControl.OnOpenGlRender) ----

    /// <summary>One instance's draw data after mode resolution (buffers shared per part).</summary>
    private readonly record struct InstanceDraw(
        EffectiveMode Mode, uint Vao, int IndexCount, uint EdgeVao, int EdgeVertexCount,
        uint WireVao, int WireVertexCount, Matrix4d Model, PartColor Color, Vector3d WorldCenter);

    private static unsafe byte[] Draw(
        GL gl, IReadOnlyList<PartInstance> instances, int width, int height, CameraState? camera, bool furniture,
        ViewStyle style, SectionAxis sectionAxis, double? sectionOffset, bool ambientOcclusion,
        IReadOnlyList<SectionPlane>? sectionPlanes, SectionCombine sectionCombine, int supersample,
        IReadOnlyList<(Vector3d A, Vector3d B)>? preview, Matrix4d? previewWorld)
    {
        // The pbuffer context is always GLES3 (ANGLE), hence the ES header.
        string header = ViewerShaders.Header(es: true);
        uint meshProgram = ViewerShaders.LinkProgram(
            gl, header + ViewerShaders.MeshVertex, header + ViewerShaders.MeshFragment, bindAttributes: true);
        uint lineProgram = ViewerShaders.LinkProgram(
            gl, header + ViewerShaders.LineVertex, header + ViewerShaders.LineFragment, bindAttributes: true);
        uint pointProgram = ViewerShaders.LinkProgram(
            gl, header + ViewerShaders.PointVertex, header + ViewerShaders.PointFragment, bindAttributes: true);
        uint bgProgram = ViewerShaders.LinkProgram(
            gl, header + ViewerShaders.BackgroundVertex, header + ViewerShaders.BackgroundFragment, bindAttributes: false);

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
        // would be 0 = fully occluded = black parts).
        RenderGeometry.SetDefaultOcclusion(gl);

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
            var (gridVao, _) = RenderGeometry.UploadLines(gl, gridVertices);
            var (axesVao, _) = RenderGeometry.UploadLines(gl, axesVertices);

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
        var uploaded = new Dictionary<Part, (uint Vao, int IndexCount, uint EdgeVao, int EdgeVertexCount,
            uint WireVao, int WireVertexCount)>();
        var draws = new List<InstanceDraw>(instances.Count);
        foreach (var instance in instances)
        {
            var part = instance.Part;
            var mode = RenderModes.Resolve(style, part.DisplayMode);
            if (!uploaded.TryGetValue(part, out var shared))
            {
                var mesh = part.GetMesh();
                uint vao = 0;
                int indexCount = 0;
                if (mode != EffectiveMode.Wireframe)
                {
                    var render = RenderMesh.CreateFlat(mesh);
                    // Baked per-vertex occlusion (cached per mesh, shared with the
                    // window pass, deterministic) — the whole AO story is vertex data,
                    // so both passes shade from identical floats.
                    (vao, _, _, _) = RenderGeometry.UploadMesh(gl, render,
                        ambientOcclusion ? Viewer.AmbientOcclusion.For(mesh, render) : null);
                    indexCount = render.Indices.Length;
                }
                uint edgeVao = 0;
                int edgeVertexCount = 0;
                if (mode is EffectiveMode.ShadedWithEdges or EffectiveMode.Translucent)
                {
                    // B-Rep-backed parts overlay their ACTUAL B-Rep edges (smooth
                    // circles at any tessellation); others fall back to mesh
                    // dihedrals — same rule as the window (Part.GetFeatureEdges).
                    var featureEdges = part.GetFeatureEdges();
                    if (featureEdges.Count > 0)
                    {
                        (edgeVao, _) = RenderGeometry.UploadLines(gl, RenderGeometry.SegmentVertices(featureEdges));
                        edgeVertexCount = featureEdges.Count * 2;
                    }
                }
                uint wireVao = 0;
                int wireVertexCount = 0;
                if (mode == EffectiveMode.Wireframe)
                {
                    var wireEdges = WireframeEdges.Extract(mesh);
                    if (wireEdges.Count > 0)
                    {
                        (wireVao, _) = RenderGeometry.UploadLines(gl, RenderGeometry.SegmentVertices(wireEdges));
                        wireVertexCount = wireEdges.Count * 2;
                    }
                }
                shared = (vao, indexCount, edgeVao, edgeVertexCount, wireVao, wireVertexCount);
                uploaded[part] = shared;
            }

            var worldBounds = instance.Bounds();
            draws.Add(new InstanceDraw(
                mode, shared.Vao, shared.IndexCount, shared.EdgeVao, shared.EdgeVertexCount,
                shared.WireVao, shared.WireVertexCount, instance.World, part.Color ?? Palette.Steel,
                worldBounds.IsEmpty ? Vector3d.Zero : worldBounds.Center));
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
        new SectionUniforms(gl, meshProgram).Write(gl, planes, sectionCombine);
        gl.Uniform1(gl.GetUniformLocation(meshProgram, "uAmbientOcclusion"),
            ambientOcclusion ? Viewer.AmbientOcclusion.Strength : 0f);
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
            CameraMath.WriteColumnMajor(d.Model, matrix);
            gl.UniformMatrix4(uModel, 1, false, matrix);
            gl.Uniform3(uColor, d.Color.R, d.Color.G, d.Color.B);
            gl.BindVertexArray(d.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)d.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        // Line overlay: feature edges for shaded-with-edges parts, full wireframe for
        // wireframe parts. Model lines are section-clipped consistently with fills.
        gl.UseProgram(lineProgram);
        lineSection.SetEnabled(gl, section);   // model lines clip with the fills
        foreach (var d in draws)
        {
            switch (d.Mode)
            {
                case EffectiveMode.ShadedWithEdges when d.EdgeVertexCount > 0:
                    CameraMath.WriteColumnMajor(d.Model, matrix);
                    gl.UniformMatrix4(uLineModel, 1, false, matrix);
                    gl.Uniform3(uLineColor, 0.09f, 0.10f, 0.11f);
                    gl.BindVertexArray(d.EdgeVao);
                    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)d.EdgeVertexCount);
                    break;
                case EffectiveMode.Wireframe when d.WireVertexCount > 0:
                    CameraMath.WriteColumnMajor(d.Model, matrix);
                    gl.UniformMatrix4(uLineModel, 1, false, matrix);
                    gl.Uniform3(uLineColor, d.Color.R, d.Color.G, d.Color.B);
                    gl.BindVertexArray(d.WireVao);
                    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)d.WireVertexCount);
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
            CameraMath.WriteColumnMajor(view, matrix);
            gl.UniformMatrix4(gl.GetUniformLocation(pointProgram, "uView"), 1, false, matrix);
            CameraMath.WriteColumnMajor(proj, matrix);
            gl.UniformMatrix4(gl.GetUniformLocation(pointProgram, "uProj"), 1, false, matrix);
            gl.Uniform1(gl.GetUniformLocation(pointProgram, "uPointSize"), 4f * supersample);
            new SectionUniforms(gl, pointProgram).Write(gl, planes, sectionCombine);
            foreach (var d in draws)
            {
                if (d.Mode != EffectiveMode.Points)
                    continue;
                CameraMath.WriteColumnMajor(d.Model, matrix);
                gl.UniformMatrix4(uPointModel, 1, false, matrix);
                gl.Uniform3(uPointColor, d.Color.R, d.Color.G, d.Color.B);
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
                CameraMath.WriteColumnMajor(d.Model, matrix);
                gl.UniformMatrix4(uModel, 1, false, matrix);
                gl.Uniform3(uColor, d.Color.R, d.Color.G, d.Color.B);
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
                CameraMath.WriteColumnMajor(d.Model, matrix);
                gl.UniformMatrix4(uLineModel, 1, false, matrix);
                gl.BindVertexArray(d.EdgeVao);
                gl.DrawArrays(PrimitiveType.Lines, 0, (uint)d.EdgeVertexCount);
            }
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
            lineProgram, uLineModel, uLineColor, uLineSectionEnabled, matrix);

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

    /// <summary>The viewer's first-visit framing: default yaw/pitch, distance from bounds.</summary>
    private static CameraState DefaultCamera(in Aabb bounds) => bounds.IsEmpty
        ? new CameraState(0.7, 0.45, 15.0, (0, 0, 0))
        : new CameraState(0.7, 0.45, CameraMath.FrameDistance(bounds), bounds.Center);
}
