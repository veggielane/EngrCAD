using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using EngrCAD.Core;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Silk.NET.Core.Contexts;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

// CameraState moved to EngrCAD.Viewer.Core (same namespace, so nothing here changed):
// it is the argument every front end hands to CameraMath, and the browser client cannot
// reference this assembly.

/// <summary>
/// OpenGL viewport rendering kernel meshes with an orbit camera.
/// Left-drag orbits, right/middle-drag pans, wheel zooms. Z is up.
/// Works on desktop GL 3.3+ and (via ANGLE on Windows) OpenGL ES 3.
/// </summary>
public sealed class ViewportControl : OpenGlControlBase
{
    private GL? _gl;
    private uint _program;
    private int _uModel, _uView, _uProj, _uColor, _uLightDir, _uEyePos, _uHighlight;
    private int _uAlpha, _uAmbientOcclusion, _uFieldColor, _uDeformScale;
    private uint _lineProgram;
    private int _uLineModel, _uLineView, _uLineProj, _uLineColor;
    private int _uLineSectionEnabled;   // raw handle for the cube/annotation overlays
    private uint _pointProgram;
    private int _uPointModel, _uPointView, _uPointProj, _uPointColor, _uPointSize, _uPointDeformScale;

    // The section-plane uniforms of each program, written through the shared
    // SectionUniforms helper so the window and the headless pass clip identically.
    private SectionUniforms _meshSection, _lineSection, _pointSection;
    private uint _bgProgram, _bgVao;
    private uint _gridVao, _gridVbo;
    private int _gridCount;
    private uint _axesVao, _axesVbo;
    private readonly List<GpuMesh> _meshes = [];
    private readonly List<bool> _visible = [];
    private readonly List<PartInstance> _instances = [];

    // GPU buffers are shared between instances of the same Part (uploaded once per
    // distinct part); this list owns them for deletion — never delete via _meshes,
    // which references the same ids once per instance. The Part is kept so switching
    // ambient occlusion on can backfill occlusion buffers without re-uploading geometry.
    private readonly record struct PartBuffers(
        Part Part, uint Vao, uint Vbo, uint Ebo, uint EdgeVao, uint EdgeVbo,
        uint WireVao, uint WireVbo, uint AoVbo, uint FieldVbo, uint DeformVbo,
        uint GhostVao, uint GhostVbo, uint GhostEbo);

    private readonly List<PartBuffers> _gpuBuffers = [];

    private readonly object _sceneLock = new();
    private (IReadOnlyList<PartInstance> Instances, bool Frame, IReadOnlyList<bool>? Visible)? _pending;

    // The view-cube orientation widget (top-right overlay); all cube logic lives in
    // ViewCube.cs — this control only steps its animation, draws it after the scene,
    // and routes clicks inside its region to it (see the ViewCube hooks below).
    private readonly ViewCube _viewCube = new();

    private double _yaw = 0.7;
    private double _pitch = 0.45;
    private double _distance = 15.0;
    private Vector3d _target = (0, 1.6, 0.2);
    private Aabb _sceneBounds = Aabb.Empty;   // all parts' world bounds, for frustum/zoom scaling
    private Point _lastPointer;
    private Point _pressPointer;
    private int _selected = -1;
    private int _hovered = -1;                          // hover highlight (see UpdateHover)
    private HoverThrottle _hoverThrottle = new(HoverThrottle.DefaultThreshold);

    private readonly record struct GpuMesh(
        uint Vao, uint Vbo, uint Ebo, int IndexCount, Matrix4d Model, (float R, float G, float B) Color,
        uint EdgeVao, uint EdgeVbo, int EdgeVertexCount,
        uint WireVao, uint WireVbo, int WireVertexCount, Vector3d WorldCenter,
        // Field display: whether this instance's mesh carries a field-colour buffer, the
        // part's own displacement exaggeration (0 when it draws undeformed), and the
        // undeformed shape's buffers when a deformed one is being shown.
        bool FieldColored, double DeformScale, uint GhostVao, int GhostIndexCount);

    // CPU-side pick data per object: triangles plus a BVH over them, in object space
    // (PickMesh, EngrCAD.Viewer.Core — the browser client raycasts through the same
    // type and the same ScenePick, so the two front ends cannot answer a click
    // differently).
    private readonly List<PickMesh> _pickData = [];

    // Refilled per pick from the parallel instance/visibility lists rather than kept in
    // sync with them: visibility, the section state and the instance list all change
    // independently, and a cached candidate list is one more thing that can be stale
    // when a click arrives. Reused so hover (which re-picks every 4 DIPs) allocates
    // nothing.
    private readonly List<PickInstance> _pickInstances = [];

    // Reusable scratch for the translucent back-to-front sort (no per-frame allocation).
    private int[] _translucentOrder = [];
    private double[] _translucentDepth = [];

    // Section-plane SDF isolines (self-contained in SectionContours.cs; this class
    // only invalidates, draws, and releases it).
    private readonly SectionContourRenderer _sectionContours = new();

    // 3D annotations (PMI) overlay + measure tool (self-contained in
    // AnnotationLayer.cs; this class feeds items, draws, and releases it).
    private readonly AnnotationLayer _annotations = new();

    // Construction-tree preview overlay (self-contained in PreviewLayer.cs; this class
    // feeds one line batch, draws, and releases it).
    private readonly PreviewLayer _preview = new();

    // Field-display colour bar (self-contained in FieldLegendLayer.cs, geometry from the
    // shared FieldLegend).
    private readonly FieldLegendLayer _legend = new();

    /// <summary>
    /// Replaces the displayed parts (one tab's worth of loose parts, each posed by its
    /// own <see cref="Part.Transform"/>). Convenience wrapper over
    /// <see cref="SetInstances"/> for hosts without assemblies.
    /// </summary>
    public void SetParts(IReadOnlyList<Part> parts, bool frame) =>
        SetInstances([.. parts.Select(p => new PartInstance(p, p.Transform, p.Name))], frame);

    /// <summary>
    /// Replaces the displayed instances (one tab's worth — <c>Tab.Instances()</c>).
    /// Thread-safe: geometry is uploaded on the next rendered frame (the GL context is
    /// only current there); instances sharing a <see cref="Part"/> share its GPU
    /// buffers and draw with their own world matrices. With <paramref name="frame"/>
    /// the camera auto-frames to the instances' bounds, otherwise it is left
    /// untouched. Parts should be pre-meshed (<c>Scene.PreMesh</c>) so no tessellation
    /// happens on the render thread.
    /// <para><paramref name="visible"/> seeds per-instance visibility with the list
    /// (null = all visible). It travels WITH the instances rather than through later
    /// <see cref="SetVisible"/> calls because the swap happens on the render thread:
    /// a host that remembers hidden rows would otherwise write them into the outgoing
    /// list and have them wiped when the new one lands.</para>
    /// </summary>
    public void SetInstances(
        IReadOnlyList<PartInstance> instances, bool frame, IReadOnlyList<bool>? visible = null)
    {
        lock (_sceneLock)
            _pending = (instances, frame, visible);
        Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    /// <summary>
    /// Re-poses the instances already shown — same parts, same order, new world matrices.
    /// The exploded-view path: <c>Tab.Instances(factor)</c> returns the same list in the
    /// same order whatever the factor, so animating an explode must NOT go through
    /// <see cref="SetInstances"/>, which deletes and re-uploads every buffer. This
    /// touches only the per-instance matrices, so N instances keep sharing ONE mesh, one
    /// GPU buffer set and one pick BVH across the whole animation.
    /// <para>Falls back to a full <see cref="SetInstances"/> when the list no longer
    /// matches part-for-part (a live reload changed the document under the animation).</para>
    /// <para><paramref name="frame"/> re-frames the camera to the NEW bounds — pass it
    /// once when an explode is switched on (parts move outside the old framing), never
    /// per animation step, where a camera chasing the geometry is unusable.</para>
    /// </summary>
    public void SetInstancePoses(IReadOnlyList<PartInstance> instances, bool frame = false)
    {
        ArgumentNullException.ThrowIfNull(instances);
        lock (_sceneLock)
        {
            if (_pending is not null || instances.Count != _instances.Count)
            {
                // Geometry is already in flight (or the shape of the document changed):
                // let the full path win rather than racing it.
                _pending = (instances, frame, null);
                Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering);
                return;
            }
            for (int i = 0; i < instances.Count; i++)
            {
                if (!ReferenceEquals(instances[i].Part, _instances[i].Part))
                {
                    _pending = (instances, frame, null);
                    Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering);
                    return;
                }
            }
            _pendingPoses = (instances, frame);
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    private (IReadOnlyList<PartInstance> Instances, bool Frame)? _pendingPoses;

    /// <summary>Applies a queued pose update on the render thread: new model matrices and
    /// world centres, refreshed scene bounds and grid, and the caches that key on
    /// positions invalidated. No GL buffer is created or destroyed.</summary>
    private void ApplyPoses(GL gl, IReadOnlyList<PartInstance> instances, bool frame)
    {
        lock (_sceneLock)
        {
            if (instances.Count != _meshes.Count)
                return;
            var bounds = Aabb.Empty;
            for (int i = 0; i < instances.Count; i++)
            {
                var worldBounds = instances[i].Bounds();
                _meshes[i] = _meshes[i] with
                {
                    Model = instances[i].World,
                    WorldCenter = worldBounds.IsEmpty ? Vector3d.Zero : worldBounds.Center,
                };
                _instances[i] = instances[i];
                bounds = bounds.Union(worldBounds);
            }
            RebuildGrid(gl, bounds);
            _sceneBounds = bounds;
            _sectionContours.Invalidate();   // the cut moved relative to the parts
            LoadAnnotations(report: false);  // annotations are posed by the instance transform
            _preview.Set(null, Matrix4d.Identity);

            if (frame && !bounds.IsEmpty)
            {
                _target = bounds.Center;
                _distance = CameraMath.FrameDistance(bounds);
            }
        }
    }

    /// <summary>Shows a message in the status overlay (used by script hosts for errors).</summary>
    public void ShowStatus(string message) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Report(message));

    /// <summary>
    /// The current camera pose. Setting it suppresses first-scene auto-framing — used
    /// by <see cref="EngrCad.ShowLive"/> to restore the view across process restarts.
    /// </summary>
    public CameraState Camera
    {
        get => new(_yaw, _pitch, _distance, _target);
        set
        {
            var clamped = CameraMath.Clamped(value, _sceneBounds);
            _yaw = clamped.Yaw;
            _pitch = clamped.Pitch;
            _distance = clamped.Distance;
            _target = clamped.Target;
            lock (_sceneLock)
            {
                // An explicit pose wins over any auto-framing still queued.
                if (_pending is { } pending)
                    _pending = (pending.Instances, false, pending.Visible);
            }
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = GL.GetApi(new LamdaNativeContext(name => gl.GetProcAddress(name)));
        _program = CompileProgram(_gl);
        _uModel = _gl.GetUniformLocation(_program, "uModel");
        _uView = _gl.GetUniformLocation(_program, "uView");
        _uProj = _gl.GetUniformLocation(_program, "uProj");
        _uColor = _gl.GetUniformLocation(_program, "uColor");
        _uLightDir = _gl.GetUniformLocation(_program, "uLightDir");
        _uEyePos = _gl.GetUniformLocation(_program, "uEyePos");
        _uHighlight = _gl.GetUniformLocation(_program, "uHighlight");
        _meshSection = new SectionUniforms(_gl, _program);
        _uAlpha = _gl.GetUniformLocation(_program, "uAlpha");
        _uAmbientOcclusion = _gl.GetUniformLocation(_program, "uAmbientOcclusion");
        _uFieldColor = _gl.GetUniformLocation(_program, "uFieldColor");
        _uDeformScale = _gl.GetUniformLocation(_program, "uDeformScale");
        // Mesh VAOs without a baked-occlusion buffer read this context-wide constant;
        // the GL default for a disabled attribute is 0, which would shade parts black.
        RenderUploads.SetDefaultOcclusion(_gl);
        // Same rule for the field-colour attribute: a part with no results reads this
        // constant, and its uFieldColor strength is 0, so it renders exactly as before
        // field display existed.
        RenderUploads.SetDefaultFieldColor(_gl);
        // And for the displacement attributes: zero, so a part with no displacement
        // result is unmoved and unshaded-differently whatever uDeformScale carries.
        RenderUploads.SetDefaultDeformation(_gl);

        _lineProgram = CompileLineProgram(_gl);
        _uLineModel = _gl.GetUniformLocation(_lineProgram, "uModel");
        _uLineView = _gl.GetUniformLocation(_lineProgram, "uView");
        _uLineProj = _gl.GetUniformLocation(_lineProgram, "uProj");
        _uLineColor = _gl.GetUniformLocation(_lineProgram, "uColor");
        _uLineSectionEnabled = _gl.GetUniformLocation(_lineProgram, "uSectionEnabled");
        _lineSection = new SectionUniforms(_gl, _lineProgram);

        _pointProgram = CompilePointProgram(_gl);
        _uPointModel = _gl.GetUniformLocation(_pointProgram, "uModel");
        _uPointView = _gl.GetUniformLocation(_pointProgram, "uView");
        _uPointProj = _gl.GetUniformLocation(_pointProgram, "uProj");
        _uPointColor = _gl.GetUniformLocation(_pointProgram, "uColor");
        _uPointSize = _gl.GetUniformLocation(_pointProgram, "uPointSize");
        _uPointDeformScale = _gl.GetUniformLocation(_pointProgram, "uDeformScale");
        _pointSection = new SectionUniforms(_gl, _pointProgram);
        // Desktop GL ignores gl_PointSize unless program point size is enabled; the
        // cap does not exist under GLES (it is always on), where enabling it errors.
        if (GlVersion.Type != GlProfileType.OpenGLES)
            _gl.Enable(EnableCap.ProgramPointSize);

        _bgProgram = CompileBackgroundProgram(_gl);
        _bgVao = _gl.GenVertexArray(); // vertexless fullscreen triangle via gl_VertexID
    }

    /// <summary>One distinct part's GPU buffers + pick data, shared by its instances.</summary>
    private readonly record struct SharedMesh(
        uint Vao, uint Vbo, uint Ebo, int IndexCount,
        uint EdgeVao, uint EdgeVbo, int EdgeVertexCount,
        uint WireVao, uint WireVbo, int WireVertexCount, PickMesh Pick, uint AoVbo,
        bool FieldColored, double DeformScale, uint GhostVao, int GhostIndexCount);

    /// <summary>Uploads an instance list, replacing existing GPU resources. Each
    /// distinct part is prepared and uploaded once (cached display mesh → RenderMesh,
    /// feature/wire edges, pick BVH); instances reference the shared buffers with
    /// per-instance model matrices. GL context must be current.</summary>
    private void ApplyInstances(
        GL gl, IReadOnlyList<PartInstance> instances, bool frame, IReadOnlyList<bool>? visible)
    {
        DeleteMeshBuffers(gl);
        List<Part> distinctParts;
        lock (_sceneLock)
        {
            _meshes.Clear();
            _pickData.Clear();
            _visible.Clear();
            _instances.Clear();
            _selected = -1;
            _hovered = -1;
            _hoverThrottle.Reset();

            var shared = new Dictionary<Part, SharedMesh>(); // Part has reference identity
            var bounds = Aabb.Empty;
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                var part = instance.Part;
                if (!shared.TryGetValue(part, out var s))
                {
                    s = UploadShared(gl, part);
                    shared[part] = s;
                }

                var color = part.Color ?? Palette.Steel;
                var worldBounds = instance.Bounds();
                _meshes.Add(new GpuMesh(
                    s.Vao, s.Vbo, s.Ebo, s.IndexCount, instance.World, (color.R, color.G, color.B),
                    s.EdgeVao, s.EdgeVbo, s.EdgeVertexCount,
                    s.WireVao, s.WireVbo, s.WireVertexCount,
                    worldBounds.IsEmpty ? Vector3d.Zero : worldBounds.Center,
                    s.FieldColored, s.DeformScale, s.GhostVao, s.GhostIndexCount));
                _pickData.Add(s.Pick);
                _visible.Add(visible is null || i >= visible.Count || visible[i]);
                _instances.Add(instance);
                bounds = bounds.Union(worldBounds);
            }

            distinctParts = [.. shared.Keys];
            RebuildGrid(gl, bounds);
            _sceneBounds = bounds;
            _sectionContours.Invalidate();   // new scene: cached SDF routes are stale
            LoadAnnotations(report: true);
            LoadFieldDisplay();              // the legend's scale, resolved once per swap
            ClearMeasurement();              // measure points reference the old scene
            _preview.Set(null, Matrix4d.Identity);   // preview referenced the old scene

            if (frame && !bounds.IsEmpty)
            {
                _target = bounds.Center;
                _distance = CameraMath.FrameDistance(bounds);
            }
        }

        // Outside the scene lock (the bake job takes its own): the new scene is already
        // drawable flat-lit, so its occlusion streams in behind the first frame.
        StartOcclusionBake(distinctParts);
    }

    /// <summary>Uploads one distinct part's buffers and registers them for deletion.</summary>
    private SharedMesh UploadShared(GL gl, Part part)
    {
        // Everything before the first GL call is the shared PartUploads.Build: the render
        // mesh, the field colour/displacement buffers, both edge sets, the pick BVH and —
        // through the delegate below — the occlusion array. What stays here is the policy
        // (all four pieces, because the view-style dropdown must never re-upload) and the
        // uploads themselves.
        var upload = PartUploads.Build(part, PartUploadRequest.All with
        {
            Fields = _showFields,
            // Only if it is ALREADY cached: TryGet never bakes, so this upload can never
            // stall the render thread. An uncached part goes up without an occlusion
            // buffer, which reads the constant 1.0 and is exactly the flat-lit shading;
            // the background bake (StartOcclusionBake) then publishes its result and
            // BackfillOcclusion attaches it on a later frame.
            Occlusion = _ambientOcclusion ? static (m, _) => Viewer.AmbientOcclusion.TryGet(m) : null,
        });
        if (upload.FieldError is { } fieldError)
            ShowStatus(fieldError);   // a display asked for and not honourable, named

        var render = upload.Render;
        var field = upload.Field;
        var (vao, vbo, ebo, aoVbo, fieldVbo, deformVbo) = RenderUploads.UploadMesh(
            gl, render, upload.Occlusion, field?.Colors, field?.Deformation);
        var (edgeVao, edgeVbo) = RenderUploads.UploadLines(gl, RenderGeometry.SegmentVertices(upload.FeatureEdges));
        var (wireVao, wireVbo) = RenderUploads.UploadLines(gl, RenderGeometry.SegmentVertices(upload.WireEdges));

        // The undeformed shape, ghosted behind the deformed one — the comparison IS the
        // point of a deformed-shape plot. It keeps its OWN buffers rather than re-drawing
        // the main VAO at scale 0, for two reasons that both say the same thing: it must
        // look like the undeformed part, so it wants the part's own face normals (the
        // displaced shading is per triangle) and no baked occlusion attached to it.
        uint ghostVao = 0, ghostVbo = 0, ghostEbo = 0;
        int ghostIndexCount = 0;
        if (upload.ShowGhost)
        {
            (ghostVao, ghostVbo, ghostEbo, _, _, _) = RenderUploads.UploadMesh(gl, render);
            ghostIndexCount = upload.IndexCount;
        }

        _gpuBuffers.Add(new PartBuffers(
            part, vao, vbo, ebo, edgeVao, edgeVbo, wireVao, wireVbo, aoVbo, fieldVbo, deformVbo,
            ghostVao, ghostVbo, ghostEbo));

        return new SharedMesh(vao, vbo, ebo, upload.IndexCount,
            edgeVao, edgeVbo, upload.FeatureEdgeVertexCount,
            wireVao, wireVbo, upload.WireEdgeVertexCount,
            upload.RequirePick, aoVbo,
            upload.FieldColored, upload.DeformScale, ghostVao, ghostIndexCount);
    }

    /// <summary>Deletes the per-part GPU buffers (once each — instances share them).</summary>
    private void DeleteMeshBuffers(GL gl)
    {
        foreach (var b in _gpuBuffers)
        {
            gl.DeleteBuffer(b.Vbo);
            gl.DeleteBuffer(b.Ebo);
            gl.DeleteVertexArray(b.Vao);
            gl.DeleteBuffer(b.EdgeVbo);
            gl.DeleteVertexArray(b.EdgeVao);
            gl.DeleteBuffer(b.WireVbo);
            gl.DeleteVertexArray(b.WireVao);
            if (b.AoVbo != 0)
                gl.DeleteBuffer(b.AoVbo);
            if (b.FieldVbo != 0)
                gl.DeleteBuffer(b.FieldVbo);
            if (b.DeformVbo != 0)
                gl.DeleteBuffer(b.DeformVbo);
            if (b.GhostVao != 0)
            {
                gl.DeleteBuffer(b.GhostVbo);
                gl.DeleteBuffer(b.GhostEbo);
                gl.DeleteVertexArray(b.GhostVao);
            }
        }
        _gpuBuffers.Clear();
    }

    /// <summary>
    /// Attaches occlusion buffers for parts that do not have one yet — the parts whose
    /// background bake has just landed, and the parts uploaded while ambient occlusion
    /// was off (the toggle turning on). Strictly non-blocking: a part whose bake is still
    /// running is skipped and picked up on a later frame, so this can run every frame.
    /// Only the occlusion buffer is created — geometry, edges and the pick BVH are
    /// untouched, so selection and hover survive both the toggle and the streaming — and
    /// the bake is cached per mesh, so toggling back and forth is free afterwards. GL
    /// context must be current (called from the render pass).
    /// </summary>
    private void BackfillOcclusion(GL gl)
    {
        bool attached = false;
        for (int i = 0; i < _gpuBuffers.Count; i++)
        {
            var buffers = _gpuBuffers[i];
            if (buffers.AoVbo != 0 || buffers.Vao == 0)
                continue;
            if (Viewer.AmbientOcclusion.TryGet(buffers.Part.GetMesh()) is not { } occlusion)
                continue;   // still baking — flat-lit for now, attached when it lands
            _gpuBuffers[i] = buffers with { AoVbo = RenderUploads.UploadOcclusion(gl, buffers.Vao, occlusion) };
            attached = true;
        }
        if (attached)
            gl.BindVertexArray(0);
    }

    // The in-flight background occlusion bake, cancelled when the scene is replaced or
    // ambient occlusion is switched off. Its own lock: ApplyInstances calls
    // StartOcclusionBake after releasing _sceneLock, and the AmbientOcclusion setter
    // takes _sceneLock first, so the two locks are always taken in the same order.
    private readonly object _occlusionLock = new();
    private CancellationTokenSource? _occlusionBake;

    /// <summary>
    /// Starts (or cancels) the background occlusion bake for the given distinct parts.
    /// Each finished part requests a frame, where <see cref="BackfillOcclusion"/> attaches
    /// it; the whole job reports once through the status overlay. Must NOT be called
    /// while holding <see cref="_sceneLock"/>.
    /// </summary>
    private void StartOcclusionBake(IReadOnlyList<Part> parts)
    {
        lock (_occlusionLock)
        {
            _occlusionBake?.Cancel();
            _occlusionBake?.Dispose();
            _occlusionBake = null;
            if (!_ambientOcclusion || parts.Count == 0)
                return;
            var cts = new CancellationTokenSource();
            _occlusionBake = cts;
            Viewer.AmbientOcclusion.BakeInBackground(
                parts,
                onPartBaked: part => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Hosts hear which part just gained its occlusion (the tree shows
                    // per-part progress); the frame then attaches it (BackfillOcclusion).
                    OcclusionBaked?.Invoke(part);
                    RequestNextFrameRendering();
                }),
                onFinished: (count, elapsed) => ShowStatus(
                    $"ambient occlusion: {count} part(s) baked in {elapsed.TotalSeconds:F1} s"),
                cts.Token);
        }
    }

    /// <summary>The distinct parts currently uploaded (bake queue input).</summary>
    private List<Part> DistinctParts()
    {
        lock (_sceneLock)
            return [.. _instances.Select(i => i.Part).Distinct()];
    }

    /// <summary>Ground grid on z = 0 sized to the scene, plus RGB world axes.</summary>
    private void RebuildGrid(GL gl, in Aabb bounds)
    {
        if (_gridVbo != 0)
        {
            gl.DeleteBuffer(_gridVbo);
            gl.DeleteVertexArray(_gridVao);
            gl.DeleteBuffer(_axesVbo);
            gl.DeleteVertexArray(_axesVao);
        }

        var (gridVertices, axesVertices) = RenderGeometry.BuildGridAndAxes(bounds);
        _gridCount = gridVertices.Length / 3;
        (_gridVao, _gridVbo) = RenderUploads.UploadLines(gl, gridVertices);
        (_axesVao, _axesVbo) = RenderUploads.UploadLines(gl, axesVertices);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        lock (_occlusionLock)
        {
            _occlusionBake?.Cancel();
            _occlusionBake?.Dispose();
            _occlusionBake = null;
        }
        if (_gl is null)
            return;
        DeleteMeshBuffers(_gl);
        _sectionContours.Release(_gl);
        _annotations.Release(_gl);
        _preview.Release(_gl);
        _legend.Release(_gl);
        _meshes.Clear();
        if (_gridVbo != 0)
        {
            _gl.DeleteBuffer(_gridVbo);
            _gl.DeleteVertexArray(_gridVao);
            _gl.DeleteBuffer(_axesVbo);
            _gl.DeleteVertexArray(_axesVao);
            _gridVbo = 0;
        }
        _viewCube.DeleteResources(_gl);
        _gl.DeleteVertexArray(_bgVao);
        _gl.DeleteProgram(_program);
        _gl.DeleteProgram(_lineProgram);
        _gl.DeleteProgram(_pointProgram);
        _gl.DeleteProgram(_bgProgram);
        _gl.Dispose();
        _gl = null;
    }

    protected override unsafe void OnOpenGlRender(GlInterface glInterface, int fb)
    {
        if (_gl is null)
            return;
        var gl = _gl;

        (IReadOnlyList<PartInstance> Instances, bool Frame, IReadOnlyList<bool>? Visible)? update = null;
        (IReadOnlyList<PartInstance> Instances, bool Frame)? poses = null;
        lock (_sceneLock)
        {
            if (_pending is not null)
            {
                update = _pending;
                _pending = null;
                _pendingPoses = null;   // the full swap supersedes any queued re-pose
            }
            else if (_pendingPoses is not null)
            {
                poses = _pendingPoses;
                _pendingPoses = null;
            }
        }
        if (update is { } u)
            ApplyInstances(gl, u.Instances, u.Frame, u.Visible);
        else if (poses is { } p)
            ApplyPoses(gl, p.Instances, p.Frame);
        if (_ambientOcclusion)
            BackfillOcclusion(gl);   // no-op unless a part was uploaded with AO off

        // View-cube hook 1: a click on the cube armed a pose animation — advance the
        // orbit camera toward it and keep frames coming until it lands.
        if (_viewCube.Step(out double animatedYaw, out double animatedPitch))
        {
            _yaw = animatedYaw;
            _pitch = animatedPitch;
            if (_viewCube.Animating)
                RequestNextFrameRendering();
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        uint width = (uint)Math.Max(1, Bounds.Width * scaling);
        uint height = (uint)Math.Max(1, Bounds.Height * scaling);
        gl.Viewport(0, 0, width, height);

        gl.ClearColor(0.11f, 0.12f, 0.14f, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // Background gradient: vertexless fullscreen triangle, no depth.
        gl.Disable(EnableCap.DepthTest);
        gl.UseProgram(_bgProgram);
        gl.BindVertexArray(_bgVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.Enable(EnableCap.DepthTest);

        var eye = CameraMath.Eye(_yaw, _pitch, _distance, _target);
        var view = CameraMath.LookAt(eye, _target, Vector3d.UnitZ);
        var proj = ProjectionMatrix((double)width / height);

        Span<float> matrix = stackalloc float[16];

        // Ground grid + world axes.
        gl.UseProgram(_lineProgram);
        CameraMath.WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(_uLineView, 1, false, matrix);
        CameraMath.WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(_uLineProj, 1, false, matrix);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(_uLineModel, 1, false, matrix);
        var activePlanes = ActiveSectionPlanes;
        _lineSection.Write(gl, activePlanes, _sectionCombine);
        _lineSection.SetEnabled(gl, false);      // grid/axes are scene furniture — never clipped
        if (_gridVbo != 0)
        {
            gl.Uniform3(_uLineColor, 0.24f, 0.26f, 0.29f);
            gl.BindVertexArray(_gridVao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_gridCount);

            gl.BindVertexArray(_axesVao);
            gl.Uniform3(_uLineColor, 0.75f, 0.30f, 0.30f);
            gl.DrawArrays(PrimitiveType.Lines, 0, 2);           // +X
            gl.Uniform3(_uLineColor, 0.33f, 0.66f, 0.33f);
            gl.DrawArrays(PrimitiveType.Lines, 2, 2);           // +Y
            gl.Uniform3(_uLineColor, 0.35f, 0.48f, 0.85f);
            gl.DrawArrays(PrimitiveType.Lines, 4, 2);           // +Z
        }

        // Shaded fills, pushed back slightly so the edge overlay wins the depth test.
        gl.UseProgram(_program);
        CameraMath.WriteColumnMajor(view, matrix);
        gl.UniformMatrix4(_uView, 1, false, matrix);
        CameraMath.WriteColumnMajor(proj, matrix);
        gl.UniformMatrix4(_uProj, 1, false, matrix);
        var lightDir = new Vector3d(-0.5, -0.7, -0.9).Normalized();
        gl.Uniform3(_uLightDir, (float)lightDir.X, (float)lightDir.Y, (float)lightDir.Z);
        gl.Uniform3(_uEyePos, (float)eye.X, (float)eye.Y, (float)eye.Z);
        _meshSection.Write(gl, activePlanes, _sectionCombine);
        gl.Uniform1(_uAmbientOcclusion, _ambientOcclusion ? Viewer.AmbientOcclusion.Strength : 0f);

        // Section mode relies on face culling staying OFF (the GL default; nothing here
        // enables CullFace): clipping a closed solid exposes its interior, and the
        // fragment shader shades those backfaces as cut material via gl_FrontFacing.
        // Fills push back slightly (polygon offset) so the edge overlay wins depth.
        gl.Uniform1(_uAlpha, 1f);
        gl.Enable(EnableCap.PolygonOffsetFill);
        gl.PolygonOffset(1f, 1f);
        bool anyPlanes = activePlanes.Count > 0;
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i])
                continue;
            var em = EffectiveModeOf(i);
            if (em is EffectiveMode.Shaded or EffectiveMode.ShadedWithEdges)
            {
                SectionFor(_meshSection, gl, i, anyPlanes);
                DrawFill(gl, i, matrix);
            }
        }
        gl.Disable(EnableCap.PolygonOffsetFill);

        // Line overlay: feature edges for shaded-with-edges parts (the CAD look),
        // full triangle wireframe for wireframe parts. Lines belong to the model, so
        // the section plane clips them consistently with the fills.
        gl.UseProgram(_lineProgram);
        bool anyPoints = false;
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i])
                continue;
            SectionFor(_lineSection, gl, i, anyPlanes);   // model lines clip with their fill
            var m = _meshes[i];
            switch (EffectiveModeOf(i))
            {
                case EffectiveMode.Wireframe when m.WireVertexCount > 0:
                    CameraMath.WriteColumnMajor(m.Model, matrix);
                    gl.UniformMatrix4(_uLineModel, 1, false, matrix);
                    var wireColor = HighlightedColor(i, m.Color);
                    gl.Uniform3(_uLineColor, wireColor.R, wireColor.G, wireColor.B);
                    gl.BindVertexArray(m.WireVao);
                    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)m.WireVertexCount);
                    break;
                case EffectiveMode.ShadedWithEdges when m.EdgeVertexCount > 0:
                    DrawFeatureEdges(gl, i, matrix);
                    break;
                case EffectiveMode.Points:
                    anyPoints = true;
                    break;
            }
        }

        // Points pass: vertex point sprites (drawn as indexed points over the same
        // mesh buffers — a flat RenderMesh references each vertex exactly once).
        if (anyPoints)
        {
            gl.UseProgram(_pointProgram);
            CameraMath.WriteColumnMajor(view, matrix);
            gl.UniformMatrix4(_uPointView, 1, false, matrix);
            CameraMath.WriteColumnMajor(proj, matrix);
            gl.UniformMatrix4(_uPointProj, 1, false, matrix);
            gl.Uniform1(_uPointSize, 4f * (float)scaling);
            _pointSection.Write(gl, activePlanes, _sectionCombine);
            for (int i = 0; i < _meshes.Count; i++)
            {
                if (!_visible[i] || EffectiveModeOf(i) != EffectiveMode.Points)
                    continue;
                SectionFor(_pointSection, gl, i, anyPlanes);
                var m = _meshes[i];
                CameraMath.WriteColumnMajor(m.Model, matrix);
                gl.UniformMatrix4(_uPointModel, 1, false, matrix);
                var pointColor = HighlightedColor(i, m.Color);
                gl.Uniform3(_uPointColor, pointColor.R, pointColor.G, pointColor.B);
                // The points view draws the mesh buffer, so it follows the displacement
                // exactly as the fills do — the CPU path gave it that for free by
                // uploading displaced positions.
                gl.Uniform1(_uPointDeformScale, (float)(m.DeformScale * _deformFactor));
                gl.BindVertexArray(m.Vao);
                gl.DrawElements(PrimitiveType.Points, (uint)m.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
        }

        // Translucent pass, after everything opaque: blended fills back-to-front by
        // part center, with depth writes off so translucent parts do not occlude each
        // other; depth *testing* stays on so opaque geometry in front still wins.
        // Order/depth scratch buffers are reused frame to frame (render paths must not
        // allocate per frame); the sort (RenderModes.SortBackToFront, shared with the
        // offscreen pass) creates no comparer delegate either.
        if (_translucentOrder.Length < _meshes.Count)
        {
            _translucentOrder = new int[_meshes.Count];
            _translucentDepth = new double[_meshes.Count];
        }
        int translucentCount = 0;
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (_visible[i] && EffectiveModeOf(i) == EffectiveMode.Translucent)
            {
                _translucentOrder[translucentCount] = i;
                _translucentDepth[translucentCount] = (_meshes[i].WorldCenter - eye).LengthSquared;
                translucentCount++;
            }
        }
        if (translucentCount > 0)
        {
            RenderModes.SortBackToFront(_translucentOrder, _translucentDepth, translucentCount);

            gl.UseProgram(_program);
            gl.Uniform1(_uAlpha, 0.4f);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(false);
            for (int k = 0; k < translucentCount; k++)
            {
                SectionFor(_meshSection, gl, _translucentOrder[k], anyPlanes);
                DrawFill(gl, _translucentOrder[k], matrix);
            }
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);

            // Their feature edges draw opaque on top (the fills wrote no depth), which
            // keeps the part's silhouette readable through the glass.
            gl.UseProgram(_lineProgram);
            for (int k = 0; k < translucentCount; k++)
            {
                if (_meshes[_translucentOrder[k]].EdgeVertexCount > 0)
                {
                    SectionFor(_lineSection, gl, _translucentOrder[k], anyPlanes);
                    DrawFeatureEdges(gl, _translucentOrder[k], matrix);
                }
            }
        }
        // Undeformed ghosts, after the opaque and translucent fills: the reference shape
        // behind a deformed-shape plot, drawn through the SAME blend/depth-mask
        // machinery translucent parts use (fainter alpha, since it is an outline rather
        // than a see-through body) and never field-coloured.
        DrawFieldGhosts(gl, matrix, anyPlanes);

        // Section-plane SDF isolines, after everything else so they read as an
        // overlay on the cut (depth test still applies; the polygon-offset fills
        // lose to coincident lines, same as feature edges).
        if (_sectionEnabled)
            DrawSectionContours(gl, matrix);

        // Annotation overlay (PMI): dimensions/notes/datums + the measure tool's
        // transient dimension, always on top (depth-off pass inside the layer) and
        // never section-clipped; before the view cube so the cube stays the topmost
        // chrome. Billboard geometry rebuilds only when the camera pose, viewport,
        // or annotation set changes (value-equality cache in the layer).
        DrawAnnotations(gl, height, scaling, matrix);

        // Construction-tree preview: the selected build step's geometry (a sketch on
        // its plane, or an intermediate operation's edges) drawn over the model, so a
        // rollback view reads against the finished part.
        _preview.Draw(gl, _lineProgram, _uLineModel, _uLineColor, _uLineSectionEnabled, matrix);

        // Field-display colour bar, with the annotations and before the view cube: it
        // is documentation about what the colours mean, so it belongs on top of the
        // model and under the orientation widget.
        _legend.Draw(gl, ActiveFieldDisplay, width, height, scaling, new LineProgramHandles(
            _lineProgram, _uLineModel, _uLineView, _uLineProj, _uLineColor, _uLineSectionEnabled));

        gl.BindVertexArray(0);

        // View-cube hook 2: the orientation widget draws last, over everything, into
        // its own top-right sub-viewport (depth cleared, ortho mini-projection); it
        // reuses the line program, so it precedes the screenshot readback and appears
        // in saved captures like the rest of the window content.
        DrawViewCube(gl, width, height, scaling);

        // A requested screenshot reads back the finished frame while the context is
        // current (GL calls are only legal here), then encodes off-thread.
        if (TakePendingScreenshot() is { } capture)
            CaptureFramebuffer(gl, (int)width, (int)height, capture);
    }

    /// <summary>The global-style x part-mode precedence, resolved by
    /// <see cref="RenderModes.Resolve"/> (shared with the offscreen pass).
    /// EffectiveDisplayMode, not DisplayMode: a Ghost part renders translucent in
    /// every front end (the debug-modifier contract).</summary>
    private EffectiveMode EffectiveModeOf(int index) =>
        RenderModes.Resolve(_viewStyle, _instances[index].Part.EffectiveDisplayMode);

    /// <summary>
    /// Draws SDF iso-distance contours on the section plane for parts with an implicit
    /// route (SDF geometry, or a Shape convertible to implicit — lowered once and
    /// cached). Geometry recomputes only when the plane (axis or offset), scene, or
    /// visibility changes. The plane is passed as its clip rule (axis, offset), which
    /// keeps the renderer plane-general.
    /// </summary>
    private void DrawSectionContours(GL gl, Span<float> matrix) =>
        _sectionContours.Draw(gl, _instances, _visible, _sectionPlanes, _sectionCombine,
            _lineProgram, _uLineModel, _uLineColor, _lineSection, matrix, _sectionReport ??= Report,
            // The window streams: extraction runs on a background task and this
            // callback (from the worker thread) schedules the frame that adopts it.
            _contourRequestRender ??=
                () => Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering));

    // Cached delegates so the per-frame contour draw does not allocate.
    private Action<string>? _sectionReport;
    private Action? _contourRequestRender;

    // ---- annotations (PMI) + measure tool (all overlay logic lives in AnnotationLayer.cs) ----

    private bool _showAnnotations = true;
    private bool _measureMode;
    private Vector3d? _measureStart;

    /// <summary>Whether part-attached annotations (dimensions, notes, datums) are
    /// drawn (default true; the measure tool's transient dimension always shows).</summary>
    public bool ShowAnnotations
    {
        get => _showAnnotations;
        set
        {
            _showAnnotations = value;
            RequestNextFrameRendering();
        }
    }

    /// <summary>True when the loaded scene carries any annotations (hosts use it for
    /// toolbar affordances).</summary>
    public bool HasAnnotations => _annotations.HasItems;

    /// <summary>
    /// Measure mode: while on, clicks pick surface points instead of selecting parts —
    /// two picks create a transient point-to-point dimension shown immediately.
    /// Escape clears the measurement; turning the mode off clears and exits.
    /// </summary>
    public bool MeasureMode
    {
        get => _measureMode;
        set
        {
            _measureMode = value;
            if (!value)
                ClearMeasurement();
            else
                Report("measure: click the first point");
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// Runs one measure pick at a control-space position — the exact path measure-mode
    /// clicks take. Public so tests and custom hosts can drive the tool directly
    /// (synthetic mouse input does not reach Avalonia).
    /// </summary>
    public void MeasurePick(Point position)
    {
        int hit = HitTest(position, out var worldPoint);
        if (hit < 0)
        {
            Report("measure: no surface under the cursor");
            return;
        }
        if (_measureStart is not { } start)
        {
            _measureStart = worldPoint;
            _annotations.SetTransient(null);
            Report($"measure: first point ({worldPoint.X:G4}, {worldPoint.Y:G4}, {worldPoint.Z:G4}) - click the second");
        }
        else
        {
            var resolved = new LinearDimension(start, worldPoint).Resolve();
            _annotations.SetTransient(resolved);
            _measureStart = null;
            Report($"measure: {resolved.Text}");
        }
        RequestNextFrameRendering();
    }

    /// <summary>
    /// One-shot measurement between two control-space positions: both points are
    /// picked through the same raycast as measure-mode clicks, the transient dimension
    /// is shown exactly as two clicks would show it, and the surface points plus their
    /// distance are returned (null when either pick misses). UI thread, like all
    /// picking — remote callers marshal. Any half-finished interactive measurement is
    /// superseded.
    /// </summary>
    public (Vector3d A, Vector3d B, double Distance)? Measure(Point first, Point second)
    {
        int hitA = HitTest(first, out var a);
        int hitB = HitTest(second, out var b);
        _measureStart = null;   // supersede a pending interactive first click
        if (hitA < 0 || hitB < 0)
        {
            Report("measure: no surface under one of the points");
            return null;
        }
        var resolved = new LinearDimension(a, b).Resolve();
        _annotations.SetTransient(resolved);
        Report($"measure: {resolved.Text}");
        RequestNextFrameRendering();
        return (a, b, (b - a).Length);
    }

    /// <summary>Clears the in-progress measure point and the transient dimension.</summary>
    private void ClearMeasurement()
    {
        _measureStart = null;
        if (_annotations.HasTransient)
        {
            _annotations.SetTransient(null);
            RequestNextFrameRendering();
        }
    }

    /// <summary>Collects the VISIBLE instances' resolved annotations for the overlay
    /// (pre-resolved off-thread by <c>Scene.PreMesh</c>; per-part failures surface in
    /// the status overlay instead of killing the scene). Re-run when visibility
    /// changes: hiding a part must hide its dimensions too, otherwise they float in
    /// empty space. Caller holds <see cref="_sceneLock"/>.</summary>
    private void LoadAnnotations(bool report)
    {
        var items = new List<AnnotationItem>();
        for (int i = 0; i < _instances.Count; i++)
        {
            var instance = _instances[i];
            if (!_visible[i] || instance.Part.Annotations.Count == 0)
                continue;
            if (instance.Part.TryResolveAnnotations(out var resolved, out string? error))
            {
                foreach (var annotation in resolved)
                    items.Add(new AnnotationItem(annotation, instance.World));
            }
            else if (report && error is not null)
                ShowStatus(error);
        }
        _annotations.SetItems(items);
    }

    /// <summary>Draws the annotation overlay (end of the render pass, before the view
    /// cube). The layer caches by camera value-equality, so a static view costs one
    /// struct comparison.</summary>
    private void DrawAnnotations(GL gl, uint heightPx, double scaling, Span<float> matrix)
    {
        var camera = AnnotationCamera.From(
            new CameraState(_yaw, _pitch, _distance, _target), _orthographic, heightPx, scaling);
        _annotations.Draw(gl, camera, _showAnnotations,
            _lineProgram, _uLineModel, _uLineColor, _uLineSectionEnabled, matrix);
    }

    // ---- construction-tree preview (all overlay logic lives in PreviewLayer.cs) ----

    /// <summary>
    /// Shows the geometry of one construction-tree row over the model — a sketch drawn
    /// on its plane, or an intermediate operation's feature edges (the rollback view) —
    /// posed by <paramref name="world"/> (the owning instance's placement). Pass null
    /// to clear. Thread-safe; the batch uploads inside the next render pass.
    /// <para>The segments must be built OFF the UI/render thread (lowering a sub-shape
    /// tessellates) — see <c>ConstructionPreviewCache</c> in EngrCAD.Modeling.</para>
    /// </summary>
    public void SetConstructionPreview(
        IReadOnlyList<(Vector3d A, Vector3d B)>? segments, Matrix4d? world = null)
    {
        _preview.Set(segments, world ?? Matrix4d.Identity);
        RequestNextFrameRendering();
    }

    /// <summary>Whether a construction preview is currently showing.</summary>
    public bool HasConstructionPreview => _preview.HasPreview;

    // ---- view cube hooks (all cube logic lives in ViewCube.cs) ----

    /// <summary>Draws the orientation widget at the end of the render pass.</summary>
    private void DrawViewCube(GL gl, uint width, uint height, double scaling) =>
        _viewCube.Draw(gl, width, height, scaling, _yaw, _pitch, new LineProgramHandles(
            _lineProgram, _uLineModel, _uLineView, _uLineProj, _uLineColor, _uLineSectionEnabled));

    /// <summary>
    /// Routes a click at a control-space position to the view cube. Returns true when
    /// the position lies inside the cube's top-right region (the region claims the
    /// click, so parts behind the widget are never picked through it); a hit on a cube
    /// face, edge, or corner animates the camera to that standard view (~250 ms eased,
    /// shortest yaw path, distance and target kept). Called by the window input
    /// handler before scene picking; public so tests and custom hosts can invoke the
    /// pick path directly (synthetic mouse input does not reach Avalonia).
    /// </summary>
    public bool ViewCubeClick(Point position)
    {
        if (!_viewCube.HandleClick(position.X, position.Y, Bounds.Width, Bounds.Height,
                _yaw, _pitch, out string? view))
            return false;
        if (view is not null)
        {
            Report($"view: {view}");
            RequestNextFrameRendering();
        }
        return true;
    }

    // ---- hover highlight (view cube faces + model parts) ----

    /// <summary>Index of the hovered part, −1 for none (the fainter pre-selection
    /// tint; a hovered selected part shows only the selection).</summary>
    public int Hovered => _hovered;

    /// <summary>
    /// Runs the hover update for a control-space position — the exact path pointer
    /// moves take (cube region first, then the throttled part raycast). Public so
    /// tests and custom hosts can drive hover directly (synthetic mouse input does
    /// not reach Avalonia).
    /// </summary>
    public void HoverAt(Point position) => UpdateHover(position);

    /// <summary>Pointer-move hover update: over the cube region the widget highlights
    /// its face/edge/corner and model hover is suppressed; elsewhere the part under
    /// the cursor is re-picked (throttled to 4+ DIPs of travel) and tinted. Redraws
    /// only when a hover state actually changes. Like click picking, hover ignores
    /// the section plane (v1) — it can hover a part through the cut-away half.</summary>
    private void UpdateHover(Point pos)
    {
        // Window-level handlers see moves over the whole window; outside the
        // viewport there is nothing to hover.
        if (pos.X < 0 || pos.Y < 0 || pos.X > Bounds.Width || pos.Y > Bounds.Height)
        {
            ClearHover();
            return;
        }

        bool insideCube = _viewCube.UpdateHover(
            pos.X, pos.Y, Bounds.Width, Bounds.Height, _yaw, _pitch, out bool cubeChanged);
        if (cubeChanged)
            RequestNextFrameRendering();
        if (insideCube)
        {
            SetHovered(-1);
            _hoverThrottle.Reset();   // leaving the widget re-picks immediately
            return;
        }

        if (!_hoverThrottle.ShouldSample(pos.X, pos.Y))
            return;
        SetHovered(HitTest(pos));
    }

    private void SetHovered(int index)
    {
        if (index == _hovered)
            return;
        _hovered = index;
        if (index >= 0)
            Report($"hover '{_instances[index].Path}'");   // the pre-selection affordance
        RequestNextFrameRendering();
    }

    /// <summary>Clears both hover highlights (a drag/press started or the pointer
    /// left the viewport).</summary>
    private void ClearHover()
    {
        if (_viewCube.ClearHover())
            RequestNextFrameRendering();
        SetHovered(-1);
        _hoverThrottle.Reset();
    }

    /// <summary>Line/point color for a part: selection gold wins, hover blends the
    /// part color toward it. The rule itself is <see cref="Highlight.LineColor"/> in
    /// EngrCAD.Viewer.Core, shared with the browser client.</summary>
    private (float R, float G, float B) HighlightedColor(int index, (float R, float G, float B) color) =>
        Highlight.LineColor(index, _selected, _hovered, color);

    /// <summary>
    /// The per-draw-group section switch, applied per PART: a part with
    /// <see cref="Part.ClippedBySection"/> false draws whole inside a cutaway (the
    /// drafting convention for fasteners and ribs). Turning the shader's master switch
    /// off is exactly what "this part is not sectioned" means — the plane set stays
    /// uploaded, so the next part clips again with no re-upload. Picking mirrors it by
    /// not consulting <see cref="SectionClip"/> for such parts, which is what keeps the
    /// clickable and the visible surface the same one.
    /// </summary>
    private void SectionFor(in SectionUniforms uniforms, GL gl, int index, bool anyPlanes) =>
        uniforms.SetEnabled(gl, anyPlanes && _instances[index].Part.ClippedBySection);

    private unsafe void DrawFill(GL gl, int index, Span<float> matrix)
    {
        var m = _meshes[index];
        CameraMath.WriteColumnMajor(m.Model, matrix);
        gl.UniformMatrix4(_uModel, 1, false, matrix);
        gl.Uniform3(_uColor, m.Color.R, m.Color.G, m.Color.B);
        // Per-draw, because it is per part: a field-coloured part reads its own colour
        // buffer, everything else stays at the neutral 0 and renders exactly as it did
        // before field display existed.
        gl.Uniform1(_uFieldColor, m.FieldColored ? FieldRendering.Strength : 0f);
        // Per-draw for the same reason: the part's own exaggeration times whatever an
        // animation's deformation track currently says. A part with no displacement
        // buffer reads zero offsets, so the value cannot reach it.
        gl.Uniform1(_uDeformScale, (float)(m.DeformScale * _deformFactor));
        // One shader knob for both states: 1.0 = selection gold, 0.35 = the fainter
        // hover tint; a hovered selected part just shows selection (Highlight.Strength,
        // shared with the browser client so both mean the same thing by "selected").
        gl.Uniform1(_uHighlight, Highlight.Strength(index, _selected, _hovered));
        gl.BindVertexArray(m.Vao);
        gl.DrawElements(PrimitiveType.Triangles, (uint)m.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    /// <summary>
    /// Draws the undeformed shape of every visible deformed part, ghosted. Its own pass
    /// after the opaque and translucent fills, with depth writes off so it never hides
    /// the result it sits behind; the geometry carries no colour buffer and the field
    /// strength is forced to 0, so it draws in the part's own colour.
    /// </summary>
    private unsafe void DrawFieldGhosts(GL gl, Span<float> matrix, bool anyPlanes)
    {
        bool any = false;
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!_visible[i] || _meshes[i].GhostVao == 0)
                continue;
            if (!any)
            {
                any = true;
                gl.UseProgram(_program);
                gl.Uniform1(_uFieldColor, 0f);
                // The ghost geometry carries no displacement buffer, so this only says
                // out loud what its zero attributes already guarantee.
                gl.Uniform1(_uDeformScale, 0f);
                gl.Uniform1(_uHighlight, 0f);
                gl.Uniform1(_uAlpha, FieldRendering.GhostAlpha);
                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                gl.DepthMask(false);
            }
            var m = _meshes[i];
            SectionFor(_meshSection, gl, i, anyPlanes);
            CameraMath.WriteColumnMajor(m.Model, matrix);
            gl.UniformMatrix4(_uModel, 1, false, matrix);
            gl.Uniform3(_uColor, m.Color.R, m.Color.G, m.Color.B);
            gl.BindVertexArray(m.GhostVao);
            gl.DrawElements(
                PrimitiveType.Triangles, (uint)m.GhostIndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        if (any)
        {
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
            gl.Uniform1(_uAlpha, 1f);
        }
    }

    private void DrawFeatureEdges(GL gl, int index, Span<float> matrix)
    {
        var m = _meshes[index];
        CameraMath.WriteColumnMajor(m.Model, matrix);
        gl.UniformMatrix4(_uLineModel, 1, false, matrix);
        if (index == _selected)
            gl.Uniform3(_uLineColor, Highlight.Selection.R, Highlight.Selection.G, Highlight.Selection.B);
        else
            gl.Uniform3(_uLineColor, 0.09f, 0.10f, 0.11f);
        gl.BindVertexArray(m.EdgeVao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)m.EdgeVertexCount);
    }

    // ---- picking ----

    /// <summary>Optional status sink for the overlay: shows the last input seen (debugging aid).</summary>
    public Action<string>? Status { get; set; }

    private void Report(string message) => Status?.Invoke(message);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Input is registered at the window level with handledEventsToo so that nothing
        // upstream (gesture recognizers, hit-test quirks over the GL surface) can starve
        // the viewport of events — trackpads proved fragile with control-level handlers.
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        top.AddHandler(PointerPressedEvent, (_, args) => HandlePressed(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerMovedEvent, (_, args) => HandleMoved(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerReleasedEvent, (_, args) => HandleReleased(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(PointerWheelChangedEvent, (_, args) => HandleWheel(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        top.AddHandler(KeyDownEvent, (_, args) => HandleKey(args),
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void Pick(Point pixel)
    {
        // Annotations first: the overlay draws on top of the model, so it picks on
        // top of it too (a claimed click never falls through to the part behind).
        if (PickAnnotation(pixel))
            return;
        int best = HitTest(pixel);
        Select(best == _selected ? -1 : best); // clicking the selection clears it
        Report(_selected >= 0 ? $"picked '{_instances[_selected].Path}'" : "picked nothing");
        SelectionChanged?.Invoke(_selected);
    }

    /// <summary>
    /// Picks a 3D annotation at a control-space position: within a few pixels of any
    /// of its drawn lines or text strokes selects it (drawn in selection gold; its
    /// text goes to the status bar), clicking it again deselects. Returns true when
    /// the click hit an annotation (the caller then skips part picking). Depth-blind
    /// like the overlay itself — an annotation you can see is pickable. Public so
    /// tests and custom hosts can drive it directly (synthetic mouse input does not
    /// reach Avalonia).
    /// </summary>
    public bool PickAnnotation(Point position)
    {
        double width = Math.Max(1, Bounds.Width);
        double height = Math.Max(1, Bounds.Height);
        var eye = CameraMath.Eye(_yaw, _pitch, _distance, _target);
        var viewProjection = ProjectionMatrix(width / height)
                           * CameraMath.LookAt(eye, _target, Vector3d.UnitZ);
        if (!ScenePick.TryRay(position.X, position.Y, width, height, viewProjection,
                out var near, out var far))
            return false;

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var camera = AnnotationCamera.From(
            new CameraState(_yaw, _pitch, _distance, _target), _orthographic,
            height * scaling, scaling);
        int hit = _annotations.Pick(camera, new Ray3d(near, far - near), _showAnnotations);
        if (hit < 0)
        {
            // Clicking empty space clears an annotation selection, then falls
            // through to normal part picking.
            if (_annotations.Select(-1))
                RequestNextFrameRendering();
            return false;
        }
        _annotations.Select(hit == _annotations.SelectedIndex ? -1 : hit);
        Report(_annotations.SelectedText is { } text
            ? $"annotation: {text.Replace('\n', ' ')}"
            : "annotation deselected");
        RequestNextFrameRendering();
        return true;
    }

    /// <summary>The selected annotation's display text (null when none) — the
    /// host-facing read of the annotation selection.</summary>
    public string? SelectedAnnotationText => _annotations.SelectedText;

    // BVH candidate scratch reused across HitTest calls (hover re-picks on pointer
    // moves — no allocation there; UI-thread only, like all input handling).
    private readonly List<int> _hitScratch = [];

    /// <summary>The nearest visible part under a control-space position (−1 for none):
    /// unprojected ray + per-part BVH + Möller–Trumbore. Shared by click picking, the
    /// hover highlight, and the measure tool. Honors the section plane: surfaces the
    /// cut removed cannot be picked through (<see cref="SectionClip"/> applies the
    /// shaders' own discard rule).</summary>
    private int HitTest(Point pixel) => HitTest(pixel, out _);

    /// <summary><see cref="HitTest(Point)"/> that also reports the world-space hit
    /// point (the measure tool's pick).</summary>
    private int HitTest(Point pixel, out Vector3d worldPoint)
    {
        double width = Math.Max(1, Bounds.Width);
        double height = Math.Max(1, Bounds.Height);
        var eye = CameraMath.Eye(_yaw, _pitch, _distance, _target);
        var viewProjection = ProjectionMatrix(width / height)
                           * CameraMath.LookAt(eye, _target, Vector3d.UnitZ);

        // Refilled per pick rather than cached: see _pickInstances.
        _pickInstances.Clear();
        for (int i = 0; i < _pickData.Count; i++)
        {
            _pickInstances.Add(new PickInstance(
                _pickData[i], _meshes[i].Model, _visible[i], _instances[i].Part.ClippedBySection));
        }

        var hit = ScenePick.Nearest(
            pixel.X, pixel.Y, width, height, viewProjection, _pickInstances,
            _sectionEnabled, _sectionPlanes, _sectionCombine, _hitScratch);
        worldPoint = hit.World;
        return hit.Index;
    }

    /// <summary>Raised when a click changes the selection (−1 = nothing); UI thread.</summary>
    public event Action<int>? SelectionChanged;

    /// <summary>Index of the selected part, −1 for none.</summary>
    public int Selected => _selected;

    /// <summary>Occurrence path of the selected instance, null for none — the
    /// name-shaped twin of <see cref="Selected"/> (remote hosts speak paths, not
    /// indices, since indices are per-published-list).</summary>
    public string? SelectedPath
    {
        get
        {
            lock (_sceneLock)
                return _selected >= 0 && _selected < _instances.Count ? _instances[_selected].Path : null;
        }
    }

    /// <summary>The displayed instances' occurrence paths, in instance order — the
    /// index-to-name mapping for hosts that address parts by path (the model tree does
    /// its own bookkeeping; the remote-control endpoint uses this).</summary>
    public IReadOnlyList<string> InstancePaths
    {
        get
        {
            lock (_sceneLock)
                return [.. _instances.Select(i => i.Path)];
        }
    }

    /// <summary>Sets the selection programmatically (tree clicks); does not raise
    /// <see cref="SelectionChanged"/>.</summary>
    public void Select(int index)
    {
        _selected = index >= 0 && index < _instances.Count ? index : -1;
        string? name = _selected >= 0 ? _instances[_selected].Path : null;
        if (VisualRoot is Window window)
            window.Title = name is not null ? $"{BaseTitle} — {name}" : BaseTitle;
        RequestNextFrameRendering();
    }

    /// <summary>Shows or hides a part (index into the current part list). A hidden
    /// part's 3D annotations are hidden with it.</summary>
    public void SetVisible(int index, bool visible)
    {
        lock (_sceneLock)
        {
            if (index < 0 || index >= _visible.Count || _visible[index] == visible)
                return;
            _visible[index] = visible;
            LoadAnnotations(report: false);   // dimensions follow their part's visibility
            LoadFieldDisplay();               // hiding the field-carrying part hides its legend
        }
        // Isolines need no nudge: SectionContourRenderer already detects the changed
        // visibility set itself (and Invalidate would drop its cached SDF lowerings).
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Changes how a part is drawn (index into the current part list). Writes through
    /// to <see cref="Part.DisplayMode"/>, so the mode survives tab switches; a live
    /// reload rebuilds parts and the model code's modes win again.
    /// </summary>
    public void SetDisplayMode(int index, DisplayMode mode)
    {
        lock (_sceneLock)
        {
            if (index >= 0 && index < _instances.Count)
                _instances[index].Part.DisplayMode = mode;
        }
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Raised on the UI thread as each part's background ambient-occlusion bake lands
    /// (the part's crevices darken on the next frame). Hosts drive per-part progress
    /// off it — the model tree clears a row's pending badge here.
    /// </summary>
    public event Action<Part>? OcclusionBaked;

    /// <summary>
    /// Sets whether a part is clipped by the section planes (index into the current
    /// part list). Writes through to <see cref="Part.ClippedBySection"/> — shared by
    /// every instance of the part, the drafting convention that fasteners and shafts
    /// draw whole inside a cutaway. Rendering and picking read the flag per frame, and
    /// the isoline overlay detects the changed flag itself (the same self-detection
    /// visibility changes use — an exempt part has no cut face to draw isolines on),
    /// so no cross-thread invalidation is needed here.
    /// </summary>
    public void SetClippedBySection(int index, bool clipped)
    {
        lock (_sceneLock)
        {
            if (index >= 0 && index < _instances.Count)
                _instances[index].Part.ClippedBySection = clipped;
        }
        RequestNextFrameRendering();
    }

    /// <summary>The display mode of a part (index into the current part list).</summary>
    public DisplayMode GetDisplayMode(int index)
    {
        lock (_sceneLock)
            return index >= 0 && index < _instances.Count
                ? _instances[index].Part.DisplayMode
                : DisplayMode.Shaded;
    }

    // ---- screenshot ----

    /// <summary>
    /// A capture the render pass has been asked for and has not performed yet: where it
    /// will be written, and — for a caller that needs to KNOW rather than be promised —
    /// the completion the write signals.
    /// </summary>
    internal sealed record PendingScreenshot(string Path, TaskCompletionSource<string>? Completion);

    private PendingScreenshot? _pendingScreenshot;

    /// <summary>
    /// Claims the armed capture, if any — what the render pass calls before reading the
    /// framebuffer back. Exactly one caller can win, so a capture is performed once and a
    /// later frame does not re-write it.
    /// <para>A method rather than the exchange written inline in the render pass, because
    /// the render pass is the one place with no test around it: this makes
    /// arm-then-consume assertable without a GL context (a test plays the render pass by
    /// taking the capture and handing it to <see cref="WriteCapture"/>), and leaves the
    /// render pass ASKING the rule instead of restating it.</para>
    /// </summary>
    internal PendingScreenshot? TakePendingScreenshot() =>
        Interlocked.Exchange(ref _pendingScreenshot, null);

    /// <summary>
    /// Saves the next rendered frame as a PNG. Thread-safe: the pixels are read inside
    /// the render pass (the only place GL calls are legal) and encoded off-thread; the
    /// resulting path (or failure) is reported through <see cref="Status"/>.
    /// With no <paramref name="path"/>, writes a timestamped file under
    /// Pictures/EngrCAD (falling back to the working directory).
    /// <para>Fire and forget — the path is a PROMISE, since the render pass has not run
    /// yet. A caller that must read the file wants
    /// <see cref="CaptureScreenshotAsync"/>.</para>
    /// </summary>
    public void SaveScreenshot(string? path = null) => ArmScreenshot(path, null);

    /// <summary>
    /// Captures the next rendered frame and completes with the PNG's path <b>once the
    /// file has been written</b> — what an RPC caller needs, since it is going to read
    /// the bytes back.
    /// <para><b>Why not the <see cref="Status"/> callback, and why not the render pass
    /// either.</b> <c>Status</c> is a BROADCAST: one event for every message this control
    /// emits, carrying prose for successes and failures alike, so a caller listening on
    /// it cannot tell its own capture from the toolbar button's, from a second concurrent
    /// request's, or from an unrelated status line — synchronising on a string is not
    /// synchronisation. And the render pass itself is too EARLY: <c>glReadPixels</c> runs
    /// there, but the encode and the write are deliberately off-thread, so a task
    /// completed at that point would hand back a path to a file that does not exist yet.
    /// The completion therefore rides the capture's own write, immediately after
    /// <c>File.WriteAllBytes</c> returns.</para>
    /// <para><b>There is no deadline here</b>, deliberately: a minimised or occluded
    /// window may not render for a long time, but how long to wait is a policy of
    /// whatever is waiting (<c>ViewportRemoteViewer</c> imposes one, because an RPC
    /// connection must not hang). What this DOES guarantee is that the task is always
    /// resolved: a request superseded by another before any frame ran fails by name
    /// rather than waiting forever.</para>
    /// </summary>
    public Task<string> CaptureScreenshotAsync(string? path = null)
    {
        // RunContinuationsAsynchronously: the completion fires on the encode task, and a
        // continuation resuming there would run caller code on a worker this class owns.
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ArmScreenshot(path, completion);
        return completion.Task;
    }

    private void ArmScreenshot(string? path, TaskCompletionSource<string>? completion)
    {
        var superseded = Interlocked.Exchange(
            ref _pendingScreenshot,
            new PendingScreenshot(path ?? DefaultScreenshotPath(), completion));
        // Only one capture can be pending, so a second request before any frame ran
        // discards the first. Fail its awaiter rather than leaving a task nothing will
        // ever complete.
        superseded?.Completion?.TrySetException(new InvalidOperationException(
            "the screenshot was superseded by another capture request before a frame was rendered"));
        Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    private static string DefaultScreenshotPath()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        folder = string.IsNullOrEmpty(folder)
            ? Environment.CurrentDirectory
            : Path.Combine(folder, "EngrCAD");
        return Path.Combine(folder, $"engrcad-{DateTime.Now:yyyyMMdd-HHmmss}.png");
    }

    /// <summary>Reads the finished frame (GL context current) and hands the pixels to
    /// <see cref="WriteCapture"/>, which encodes and writes them off-thread.</summary>
    private unsafe void CaptureFramebuffer(GL gl, int width, int height, PendingScreenshot capture)
    {
        var pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _ = WriteCapture(width, height, pixels, capture, ShowStatus);
    }

    /// <summary>
    /// Encodes a captured frame and writes it, off the render thread, then resolves the
    /// capture's completion and reports through the status callback.
    /// <para><b>The ORDER is the contract</b>: the completion is set after
    /// <c>File.WriteAllBytes</c> returns and before the status line, so a caller that
    /// awaits it may read the file immediately. Separated from
    /// <see cref="CaptureFramebuffer"/> — which needs a GL context — precisely so that
    /// ordering is testable without a window.</para>
    /// <para>Returns the encode/write task so a test can await the whole capture; the
    /// render pass discards it.</para>
    /// </summary>
    internal static Task WriteCapture(
        int width, int height, byte[] pixels, PendingScreenshot capture, Action<string> report) =>
        Task.Run(() =>
        {
            try
            {
                // GL rows are bottom-up; the framebuffer alpha is compositing residue.
                var png = PngWriter.Encode(width, height, pixels, flipVertically: true, forceOpaque: true);
                if (Path.GetDirectoryName(capture.Path) is { Length: > 0 } directory)
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(capture.Path, png);
                capture.Completion?.TrySetResult(capture.Path);
                report($"screenshot saved: {capture.Path}");
            }
            catch (Exception e)
            {
                capture.Completion?.TrySetException(e);
                report($"screenshot failed: {e.Message}");
            }
        });

    /// <summary>Zoom-to-fit: frames the camera on the visible parts' bounds.</summary>
    public void Frame()
    {
        var bounds = Aabb.Empty;
        lock (_sceneLock)
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                if (_visible[i])
                    bounds = bounds.Union(_instances[i].Bounds());
            }
        }
        if (!bounds.IsEmpty)
        {
            _target = bounds.Center;
            _distance = CameraMath.FrameDistance(bounds);
        }
        RequestNextFrameRendering();
    }

    /// <summary>Window title stem, restored when nothing is selected.</summary>
    public string BaseTitle { get; set; } = "EngrCAD";

    private ViewStyle _viewStyle = ViewStyle.ShadedWithEdges;

    /// <summary>
    /// Global view style for the whole viewport (points / wireframe / shaded / shaded
    /// with edges; default <see cref="ViewStyle.ShadedWithEdges"/>). Sets how parts
    /// render by default; a part with an explicitly non-default
    /// <see cref="Part.DisplayMode"/> (Wireframe or Translucent) keeps its own mode —
    /// see <see cref="Viewer.ViewStyle"/> for the precedence rule.
    /// </summary>
    public ViewStyle ViewStyle
    {
        get => _viewStyle;
        set
        {
            _viewStyle = value;
            RequestNextFrameRendering();
        }
    }

    private bool _showFields = true;

    /// <summary>
    /// Whether parts that carry simulation results are drawn through their
    /// <c>Part.FieldDisplay</c> (default true — a scene that states a field display
    /// shows it). Switching it off returns every part to its own colour and undeformed
    /// shape.
    /// <para>Unlike the other view toggles this one <b>re-uploads</b>: colours and
    /// displacements are vertex attributes, so switching them on or off is a change of
    /// buffers. Changing the AMOUNT of displacement is not — that is
    /// <see cref="DeformFactor"/>, one uniform.</para>
    /// </summary>
    public bool ShowFields
    {
        get => _showFields;
        set
        {
            if (_showFields == value)
                return;
            _showFields = value;
            lock (_sceneLock)
            {
                if (_instances.Count > 0)
                    _pending = ([.. _instances], false, [.. _visible]);
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(RequestNextFrameRendering);
        }
    }

    private double _deformFactor = 1;

    /// <summary>
    /// A multiplier on every part's own <c>FieldDisplay.DeformScale</c> — 1 (the
    /// default) draws each part at the exaggeration it states, 0 draws every result
    /// undeformed.
    /// <para><b>This is the animation seam for a structural result</b>, and it costs one
    /// float uniform per frame: the displacement rides as a vertex attribute, so nothing
    /// is re-uploaded, no buffer is touched and the instance list is untouched — exactly
    /// the property that lets <c>SetInstancePoses</c> animate an exploded view with
    /// matrices alone. A <c>DeformationTrack</c> on an <see cref="Animation"/> drives it.
    /// </para>
    /// <para>What deliberately does NOT follow it: the pick BVH, which is built once at
    /// each part's own scale (see <c>FieldRendering.PickShape</c>), and the feature-edge
    /// overlay, which a part carrying a displacement never draws at any factor.</para>
    /// </summary>
    public double DeformFactor
    {
        get => _deformFactor;
        set
        {
            if (_deformFactor == value)
                return;
            _deformFactor = value;
            RequestNextFrameRendering();
        }
    }

    private ResolvedFieldDisplay? _activeField;

    /// <summary>
    /// The field display currently shown, if any — the first visible instance whose
    /// part resolves one. Hosts read it for the legend and the min/max readout; it is
    /// null when nothing shows a field (or <see cref="ShowFields"/> is off).
    /// <para>Deliberately ONE display rather than a per-part list: the legend is a
    /// single scale, and several parts on different scales under one bar would be a
    /// legend that lies. Parts sharing a scale is what an explicit
    /// <c>FieldDisplay.Range</c> is for.</para>
    /// <para>Resolved when the instances or their visibility change, never per frame:
    /// resolution takes the PART's lock, and the render thread must not be able to queue
    /// behind a mesh in flight on another thread — the rule <see cref="Part.HasMesh"/>
    /// exists for.</para>
    /// <para>Reported at the EFFECTIVE exaggeration (the part's own times
    /// <see cref="DeformFactor"/>), because the legend's title states that number and a
    /// legend saying "40X DEFORMED" over a frame drawn at 20X would be exactly the lie
    /// the title exists to prevent. With no animation the factor is 1, so the value is
    /// the display's own, unchanged.</para>
    /// </summary>
    public ResolvedFieldDisplay? ActiveFieldDisplay =>
        _showFields ? FieldRendering.AtFactor(_activeField, _deformFactor) : null;

    /// <summary>Re-resolves <see cref="ActiveFieldDisplay"/> from the visible instances.
    /// Caller holds <see cref="_sceneLock"/>, off the render thread or at a scene
    /// swap.</summary>
    private void LoadFieldDisplay()
    {
        _activeField = null;
        for (int i = 0; i < _instances.Count; i++)
        {
            if (_visible[i] && _instances[i].Part.TryResolveFieldDisplay(out var resolved, out _))
            {
                _activeField = resolved;
                return;
            }
        }
    }

    private bool _ambientOcclusion = EngrCadOptions.AmbientOcclusionDefault;

    /// <summary>
    /// Baked ambient occlusion: pockets, bores, ribs and fillet roots darken where the
    /// part occludes itself (default <see cref="EngrCadOptions.AmbientOcclusionDefault"/>;
    /// the toolbar's AO toggle and <see cref="EngrCadOptions.AmbientOcclusion"/> drive
    /// it). The occlusion is baked per part on the CPU and uploaded as a vertex
    /// attribute, so it costs nothing per frame and the headless pass shades from the
    /// same numbers; switching it off reproduces the flat-lit look exactly.
    /// <para>The bake is NOT waited for: the scene draws immediately flat-lit and each
    /// part darkens as its bake lands (cheapest first), so opening a window or turning
    /// this on never stalls. Nothing else about the scene, selection, or camera changes
    /// meanwhile, and the bake is cached per mesh, so toggling back and forth after the
    /// first pass is instant.</para>
    /// </summary>
    public bool AmbientOcclusion
    {
        get => _ambientOcclusion;
        set
        {
            if (_ambientOcclusion == value)
                return;
            _ambientOcclusion = value;
            // Switching on queues the missing bakes; switching off cancels the queue
            // (StartOcclusionBake reads the new flag) and the shader stops mixing the
            // attribute in, so the uploaded buffers just go unused.
            StartOcclusionBake(DistinctParts());
            RequestNextFrameRendering();
        }
    }

    private bool _orthographic;

    /// <summary>Orthographic (true) vs perspective (false, default) projection. The
    /// orthographic frustum is sized so the target plane keeps its apparent size.</summary>
    public bool Orthographic
    {
        get => _orthographic;
        set
        {
            _orthographic = value;
            RequestNextFrameRendering();
        }
    }

    // ---- section plane ----

    private bool _sectionEnabled;
    private SectionAxis _sectionAxis = SectionAxis.Z;
    private double _sectionOffset;
    private bool _sectionOffsetSet;
    private int _sectionPlaneCount = 1;
    private SectionCombine _sectionCombine = SectionCombine.Intersection;

    // The plane set actually uploaded. It is rebuilt from the axis/offset/count model
    // whenever those change, and replaced wholesale by the SectionPlanes setter (which
    // is how a host places planes the X/Y/Z model cannot express).
    private readonly List<SectionPlane> _sectionPlanes = [];

    /// <summary>The planes the shader clips with this frame (none when section mode is
    /// off, which makes the uniform write the whole switch).</summary>
    private IReadOnlyList<SectionPlane> ActiveSectionPlanes =>
        _sectionEnabled ? _sectionPlanes : [];

    /// <summary>
    /// Section mode: clips the model at the current <see cref="SectionPlanes"/> to
    /// reveal interiors — exposed backfaces render as flat cut material. When first
    /// enabled, one plane is placed at the middle of the current parts' bounds along
    /// <see cref="SectionAxis"/>. Picking ignores section planes (v1).
    /// </summary>
    public bool SectionEnabled
    {
        get => _sectionEnabled;
        set
        {
            _sectionEnabled = value;
            if (value && (_sectionPlanes.Count == 0 || !_sectionOffsetSet))
            {
                if (!_sectionOffsetSet)
                    ResetSectionOffset();
                RebuildSectionPlanes();
            }
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// Which world axis the primary section plane is perpendicular to (default Z — the
    /// classic horizontal section). Changing the axis re-centers the plane at the middle
    /// of the parts' bounds along the new axis (one offset is meaningless on another
    /// axis) and rebuilds the axis-aligned plane set, so a quarter or octant cut follows
    /// the new starting axis.
    /// </summary>
    public SectionAxis SectionAxis
    {
        get => _sectionAxis;
        set
        {
            if (_sectionAxis != value)
            {
                _sectionAxis = value;
                ResetSectionOffset();
                RebuildSectionPlanes();
            }
            RequestNextFrameRendering();
        }
    }

    /// <summary>Offset of the primary (first) section plane along its normal; geometry
    /// beyond it is clipped. Other planes keep their own offsets.</summary>
    public double SectionOffset
    {
        get => _sectionOffset;
        set
        {
            _sectionOffset = value;
            _sectionOffsetSet = true;
            if (_sectionPlanes.Count == 0)
                RebuildSectionPlanes();
            else
                _sectionPlanes[0] = _sectionPlanes[0] with { Offset = value };
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// How many axis-aligned section planes are active: 1 is the classic single cut,
    /// **2 the quarter cut**, 3 an octant. The planes start at <see cref="SectionAxis"/>
    /// and take the following world axes in X-Y-Z cyclic order, each centered in the
    /// parts' bounds (the primary plane keeps <see cref="SectionOffset"/>). Combined by
    /// <see cref="SectionCombine"/>. Setting this replaces any custom
    /// <see cref="SectionPlanes"/>.
    /// </summary>
    public int SectionPlaneCount
    {
        get => _sectionPlaneCount;
        set
        {
            _sectionPlaneCount = Math.Clamp(value, 1, ViewerShaders.MaxSectionPlanes);
            if (!_sectionOffsetSet)
                ResetSectionOffset();
            RebuildSectionPlanes();
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// The active section planes. Read it to inspect what the shader clips with; set it
    /// to place planes the axis model cannot express (arbitrary normals). Setting also
    /// updates <see cref="SectionPlaneCount"/>; changing <see cref="SectionAxis"/> or
    /// <see cref="SectionPlaneCount"/> afterwards rebuilds the axis-aligned set again.
    /// At most <see cref="ViewerShaders.MaxSectionPlanes"/> planes are uploaded.
    /// </summary>
    public IReadOnlyList<SectionPlane> SectionPlanes
    {
        get => _sectionPlanes;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _sectionPlanes.Clear();
            _sectionPlanes.AddRange(value);
            _sectionPlaneCount = Math.Max(1, _sectionPlanes.Count);
            if (_sectionPlanes.Count > 0)
            {
                _sectionOffset = _sectionPlanes[0].Offset;
                _sectionOffsetSet = true;
            }
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// How several planes combine (default <see cref="SectionCombine.Intersection"/> —
    /// the CAD-standard quarter/octant cutaway). Irrelevant with a single plane, where
    /// both rules coincide.
    /// </summary>
    public SectionCombine SectionCombine
    {
        get => _sectionCombine;
        set
        {
            _sectionCombine = value;
            RequestNextFrameRendering();
        }
    }

    /// <summary>Rebuilds the axis-aligned plane set from axis + offset + count: the
    /// primary plane keeps the current offset, the rest are centered in the parts'
    /// bounds along the following world axes.</summary>
    private void RebuildSectionPlanes()
    {
        var bounds = PartsBounds();
        _sectionPlanes.Clear();
        for (int i = 0; i < _sectionPlaneCount; i++)
        {
            var axis = (SectionAxis)(((int)_sectionAxis + i) % 3);
            double offset = i == 0
                ? _sectionOffset
                : bounds.IsEmpty ? 0 : AxisComponent(bounds.Center, axis);
            _sectionPlanes.Add(SectionPlane.On(axis, offset));
        }
    }

    /// <summary>Legacy name for <see cref="SectionOffset"/> from when section planes
    /// were Z-only (kept so existing hosts compile; identical semantics when
    /// <see cref="SectionAxis"/> is Z, the default).</summary>
    public double SectionHeight
    {
        get => SectionOffset;
        set => SectionOffset = value;
    }

    /// <summary>Centers the primary section plane in the parts' bounds along the active
    /// axis.</summary>
    private void ResetSectionOffset()
    {
        var bounds = PartsBounds();
        if (bounds.IsEmpty)
            return;
        _sectionOffset = AxisComponent(bounds.Center, _sectionAxis);
        _sectionOffsetSet = true;
    }

    private static double AxisComponent(in Vector3d v, SectionAxis axis) => axis switch
    {
        SectionAxis.X => v.X,
        SectionAxis.Y => v.Y,
        _ => v.Z,
    };

    /// <summary>Moves the section plane by 2% of the parts' extent along the active
    /// axis per step.</summary>
    private void NudgeSection(int direction)
    {
        var bounds = PartsBounds();
        double extent = bounds.IsEmpty ? 10 : AxisComponent(bounds.Size, _sectionAxis);
        SectionOffset += direction * extent * 0.02;
        Report($"section {_sectionAxis.ToString().ToLowerInvariant()} = {_sectionOffset:G4}");
    }

    /// <summary>Union of all parts' world bounds (empty when no scene is loaded).</summary>
    private Aabb PartsBounds()
    {
        var bounds = Aabb.Empty;
        lock (_sceneLock)
        {
            foreach (var instance in _instances)
                bounds = bounds.Union(instance.Bounds());
        }
        return bounds;
    }

    private Matrix4d ProjectionMatrix(double aspect)
    {
        // Near/far scale with the camera and scene (shared with the offscreen pass),
        // so large scenes neither clip at a fixed far plane nor need a distance clamp.
        var (near, far) = CameraMath.FrustumPlanes(_distance, _sceneBounds);
        return _orthographic
            ? CameraMath.Orthographic(_distance * Math.Tan(Math.PI / 8), aspect, near, far)
            : CameraMath.Perspective(Math.PI / 4, aspect, near, far);
    }

    // Shader sources live in ViewerShaders (RenderCore.cs), shared verbatim with the
    // headless OffscreenRenderer so the two passes cannot drift; only the version
    // header differs (desktop GL 3.3 vs GLES3 via ANGLE, chosen at runtime here).

    private string ShaderHeader => ViewerShaders.Header(GlVersion.Type == GlProfileType.OpenGLES);

    private uint CompileProgram(GL gl) => ViewerPrograms.LinkProgram(
        gl, ShaderHeader + ViewerShaders.MeshVertex, ShaderHeader + ViewerShaders.MeshFragment,
        bindAttributes: true);

    private uint CompileLineProgram(GL gl) => ViewerPrograms.LinkProgram(
        gl, ShaderHeader + ViewerShaders.LineVertex, ShaderHeader + ViewerShaders.LineFragment,
        bindAttributes: true);

    private uint CompilePointProgram(GL gl) => ViewerPrograms.LinkProgram(
        gl, ShaderHeader + ViewerShaders.PointVertex, ShaderHeader + ViewerShaders.PointFragment,
        bindAttributes: true);

    private uint CompileBackgroundProgram(GL gl) => ViewerPrograms.LinkProgram(
        gl, ShaderHeader + ViewerShaders.BackgroundVertex, ShaderHeader + ViewerShaders.BackgroundFragment,
        bindAttributes: false);

    // ---- camera ----

    private void HandlePressed(PointerPressedEventArgs e)
    {
        _lastPointer = e.GetPosition(this);
        _pressPointer = _lastPointer;
        // Remember whether the gesture began on the view cube: a drag that started
        // there rotate-snaps on release (commercial-cube behavior), while a drag
        // starting anywhere else orbits freely.
        _pressedOnCube = ViewCube.InRegion(_pressPointer.X, _pressPointer.Y, Bounds.Width, Bounds.Height);
        ClearHover();   // a press starts a click or drag; hover returns on the next move
        Report($"press at {_pressPointer.X:F0},{_pressPointer.Y:F0}");
    }

    private bool _pressedOnCube;

    private void HandleMoved(PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var pos = point.Position;
        var delta = pos - _lastPointer;
        _lastPointer = pos;

        // Laptop-friendly: everything reachable from the primary button + modifiers.
        // Shift+drag (or right/middle drag) pans, Ctrl+drag zooms, plain drag orbits.
        bool pan = point.Properties.IsRightButtonPressed
                   || point.Properties.IsMiddleButtonPressed
                   || (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        bool zoom = point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (pan)
        {
            Move(CameraMath.DragPan(Camera, delta.X, delta.Y));
            Report("pan (drag)");
        }
        else if (zoom)
        {
            Move(CameraMath.DragZoom(Camera, delta.Y, _sceneBounds));
            Report("zoom (drag)");
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            Move(CameraMath.DragOrbit(Camera, delta.X, delta.Y));
            Report("orbit (drag)");
        }
        else
            UpdateHover(pos);   // no buttons: hover highlight (cube region or part)
    }

    private void HandleReleased(PointerReleasedEventArgs e)
    {
        var pos = e.GetPosition(this);
        var moved = pos - _pressPointer;
        // Click-vs-drag discrimination: a release within 4 DIPs of the press (16 =
        // 4 DIP squared, compared against the squared travel to skip the sqrt) is a
        // click that picks; anything farther was an orbit/pan/zoom drag. A UI feel
        // threshold matching HoverThrottle's 4-DIP re-pick distance — not a Tolerance
        // decision, and DIP-based so it is monitor-scale independent.
        if (moved.X * moved.X + moved.Y * moved.Y < 16)
        {
            // View-cube pre-check: clicks inside the cube region go to the widget
            // (view change), never through it to scene picking. Drags are untouched —
            // dragging on the cube orbits via the normal orbit handling above. In
            // measure mode, clicks pick surface points instead of selecting parts.
            if (!ViewCubeClick(pos))
            {
                if (_measureMode)
                    MeasurePick(pos);
                else
                    Pick(pos);
            }
        }
        else if (_pressedOnCube)
            SnapViewCube();   // dragging ON the cube settles onto a standard view
        else
            Report("release (drag end)");
        _pressedOnCube = false;
    }

    /// <summary>
    /// Rotate-snap: animates the camera to the standard orientation nearest the current
    /// pose (the face/edge/corner you ended up closest to) — what a drag that started
    /// on the view cube does on release. Public so tests and custom hosts can drive it
    /// directly (synthetic mouse input does not reach Avalonia).
    /// </summary>
    public void SnapViewCube()
    {
        Report($"view: snapped to {_viewCube.SnapToNearest(_yaw, _pitch)}");
        RequestNextFrameRendering();
    }

    private void HandleWheel(PointerWheelEventArgs e)
    {
        // Trackpad two-finger scrolls arrive as many small fractional deltas.
        double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        Move(CameraMath.WheelZoom(Camera, delta, _sceneBounds));
        Report($"wheel Δ{delta:F2}");
    }

    private void HandleKey(KeyEventArgs e)
    {
        const double step = CameraMath.KeyStep;
        switch (e.Key)
        {
            case Key.Left: Orbit(step, 0); break;
            case Key.Right: Orbit(-step, 0); break;
            case Key.Up when !e.KeyModifiers.HasFlag(KeyModifiers.Shift): Orbit(0, step); break;
            case Key.Down when !e.KeyModifiers.HasFlag(KeyModifiers.Shift): Orbit(0, -step); break;
            case Key.OemPlus or Key.Add or Key.PageUp: Zoom(0.88); break;
            case Key.OemMinus or Key.Subtract or Key.PageDown: Zoom(1 / 0.88); break;
            case Key.W: Pan(0, 12); break;
            case Key.S: Pan(0, -12); break;
            case Key.A: Pan(12, 0); break;
            case Key.D: Pan(-12, 0); break;
            case Key.OemOpenBrackets when SectionEnabled: NudgeSection(-1); return;  // reports the height itself
            case Key.OemCloseBrackets when SectionEnabled: NudgeSection(+1); return;
            case Key.Escape when _measureMode:
                ClearMeasurement();
                Report("measure: cleared");
                return;
            default: return;
        }
        Report($"key {e.Key}");
    }

    // Every camera move goes through CameraMath (EngrCAD.Viewer.Core), which is what the
    // Blazor client calls too — the two front ends cannot feel different, because there
    // is only one implementation of what a drag does.

    private void Orbit(double yawDelta, double pitchDelta) =>
        Move(CameraMath.Orbit(Camera, yawDelta, pitchDelta));

    private void Zoom(double factor) =>
        Move(CameraMath.Zoom(Camera, factor, _sceneBounds));

    private void Pan(double dx, double dy) =>
        Move(CameraMath.Pan(Camera, dx, dy));

    /// <summary>Adopts an already-legal pose and asks for a frame (the pose setter's
    /// clamping and auto-frame suppression are for externally supplied poses).</summary>
    private void Move(CameraState next)
    {
        _yaw = next.Yaw;
        _pitch = next.Pitch;
        _distance = next.Distance;
        _target = next.Target;
        RequestNextFrameRendering();
    }
}
