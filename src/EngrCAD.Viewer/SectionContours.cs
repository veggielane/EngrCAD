using EngrCAD.Core;
using EngrCAD.Modeling;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

// The GL half of the section-plane SDF isolines. The pure half — SectionContours
// (extraction, plane frames, the SDF route, the three family colours) and
// SectionContourGeometry — lives in EngrCAD.Viewer.Core so the browser client shares
// it; ViewportControl only calls Invalidate/Draw/Release here.

/// <summary>
/// One completed background contour build: the geometries (one per section plane),
/// the per-part lowering failures encountered, and the exact target (planes +
/// visibility) the build was made for, so the render thread can adopt it as the new
/// built state. <see cref="Generation"/> stamps the build so a target that changed
/// while the worker ran is discarded rather than adopted (the TabMeshLoader rule:
/// a stale result must never land).
/// </summary>
internal sealed record SectionContourBuild(
    int Generation, List<SectionContourGeometry> Geometries,
    List<(Part Part, string Message)> Failures, List<SectionPlane> Planes,
    bool[] Visible, bool[] Clipped);

/// <summary>
/// The UI-free state machine behind the streaming isoline rebuild: tracks which
/// target (planes + visibility) is being built on the background task, stamps builds
/// with a generation so <see cref="Invalidate"/> or a newer kick rejects an in-flight
/// result, and hands completed builds back for adoption on the render thread.
/// Extraction itself (<see cref="SectionContours.Build"/> — marching squares plus the
/// first <c>Part.TryGetSdf</c> lowering, which can be seconds for a bridged shape)
/// runs entirely off-thread; the render thread only adopts and uploads. All methods
/// except the worker's own completion write are render-thread-only.
/// </summary>
internal sealed class SectionContourWorker
{
    private int _generation;
    private List<SectionPlane>? _inFlightPlanes;
    private bool[]? _inFlightVisible;
    private bool[]? _inFlightClipped;
    private SectionContourBuild? _completed;
    private readonly object _resultLock = new();

    /// <summary>Per-instance <see cref="Part.ClippedBySection"/> snapshot — part of the
    /// build target, because an exempt part contributes no isolines and the flag can be
    /// toggled from the tree (<c>ViewportControl.SetClippedBySection</c>).</summary>
    internal static bool[] ClippedBits(IReadOnlyList<PartInstance> instances)
    {
        var bits = new bool[instances.Count];
        for (int i = 0; i < instances.Count; i++)
            bits[i] = instances[i].Part.ClippedBySection;
        return bits;
    }

    /// <summary>True while a build for some target is running and its result has not
    /// been adopted or superseded.</summary>
    public bool Building => _inFlightPlanes is not null;

    /// <summary>Rejects any in-flight build's result (scene swap, live reload): its
    /// generation no longer matches, so <see cref="TryAdopt"/> discards it.</summary>
    public void Invalidate()
    {
        _generation++;
        _inFlightPlanes = null;
        _inFlightVisible = null;
        _inFlightClipped = null;
    }

    /// <summary>
    /// Starts a background build for the given target unless one for the SAME target
    /// is already running (compared by exact plane equality and visibility bits — the
    /// same staleness rule the renderer uses). <paramref name="requestRender"/> is
    /// invoked from the worker when the result is ready, so the host can schedule a
    /// frame that adopts it. Snapshots the visibility list (mutated in place by the
    /// UI thread) and the plane list; the instance list is replaced wholesale on
    /// change (and any change routes through <see cref="Invalidate"/>), so its
    /// reference is safe to carry.
    /// </summary>
    public void EnsureBuilding(
        IReadOnlyList<PartInstance> instances, IReadOnlyList<bool> visible,
        IReadOnlyList<SectionPlane> planes, Action requestRender)
    {
        bool[] clipped = ClippedBits(instances);
        if (SameTarget(_inFlightPlanes, _inFlightVisible, _inFlightClipped, planes, visible, clipped))
            return;
        _generation++;
        int generation = _generation;
        var planesCopy = new List<SectionPlane>(planes);
        bool[] visibleCopy = [.. visible];
        _inFlightPlanes = planesCopy;
        _inFlightVisible = visibleCopy;
        _inFlightClipped = clipped;
        Task.Run(() =>
        {
            var geometries = new List<SectionContourGeometry>(planesCopy.Count);
            var failures = new List<(Part, string)>();
            foreach (var plane in planesCopy)
            {
                geometries.Add(SectionContours.Build(
                    instances, visibleCopy,
                    SectionContours.PlaneFrame(plane.Normal.Normalized(), plane.Offset),
                    (part, message) => failures.Add((part, message))));
            }
            lock (_resultLock)
            {
                // Never let an older task's late finish overwrite a newer result:
                // only the highest completed generation can survive to adoption.
                if (_completed is null || _completed.Generation < generation)
                    _completed = new SectionContourBuild(
                        generation, geometries, failures, planesCopy, visibleCopy, clipped);
            }
            requestRender();
        });
    }

    /// <summary>
    /// The completed build if it is still current, else null (a stale build — its
    /// target superseded or <see cref="Invalidate"/>d — is discarded either way).
    /// Called on the render thread; adoption clears the in-flight target so the next
    /// change kicks a fresh build.
    /// </summary>
    public SectionContourBuild? TryAdopt()
    {
        SectionContourBuild? completed;
        lock (_resultLock)
        {
            completed = _completed;
            _completed = null;
        }
        if (completed is null || completed.Generation != _generation)
            return null;
        _inFlightPlanes = null;
        _inFlightVisible = null;
        _inFlightClipped = null;
        return completed;
    }

    private static bool SameTarget(
        List<SectionPlane>? builtPlanes, bool[]? builtVisible, bool[]? builtClipped,
        IReadOnlyList<SectionPlane> planes, IReadOnlyList<bool> visible, bool[] clipped)
    {
        if (builtPlanes is null || builtVisible is null || builtClipped is null)
            return false;
        if (planes.Count != builtPlanes.Count || visible.Count != builtVisible.Length
            || clipped.Length != builtClipped.Length)
            return false;
        for (int i = 0; i < planes.Count; i++)
        {
            // Exact equality on purpose: change detection, not geometry.
            if (planes[i] != builtPlanes[i])
                return false;
        }
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i] != builtVisible[i])
                return false;
        }
        for (int i = 0; i < clipped.Length; i++)
        {
            if (clipped[i] != builtClipped[i])
                return false;
        }
        return true;
    }
}

/// <summary>
/// GL-side owner of the section-isoline overlay: caches SDF routes and the built
/// geometry, rebuilds only when the section plane moves or the scene/visibility
/// changes (never per frame), and draws three colored line batches through the shared
/// line program. In the window the rebuild STREAMS: extraction (marching squares plus
/// the first <c>Part.TryGetSdf</c> lowering, which used to stall the first
/// section-enabled frame for seconds on a bridged shape) runs on a background task
/// (the <see cref="AmbientOcclusion.BakeInBackground"/> precedent) and the previous
/// contours — or nothing, on first enable — draw until the new ones land; the
/// offscreen pass (no <c>requestRender</c>) builds inline, one-shot and
/// deterministic. All GL-touching methods must be called with the context current
/// (render pass).
/// </summary>
internal sealed class SectionContourRenderer
{
    // Parts whose implicit lowering failed and has already been reported. The failure
    // itself is cached on the Part now, so without this set every rebuild would repeat
    // the same status line.
    private readonly HashSet<Part> _reportedFailures = [];
    private readonly List<SectionContourGeometry> _geometries = [];
    private readonly List<(uint ZeroVao, uint ZeroVbo, uint PositiveVao, uint PositiveVbo,
        uint NegativeVao, uint NegativeVbo)> _buffers = [];
    private bool _dirty = true;
    private readonly List<SectionPlane> _builtPlanes = [];
    // Scratch for the per-plane sibling clip set, reused frame to frame (the render
    // paths must not allocate per frame).
    private readonly List<SectionPlane> _siblings = [];
    private bool[] _builtVisible = [];
    private int _builtVisibleCount;
    private bool[] _builtClipped = [];
    private int _reportedParts = -1;
    private readonly SectionContourWorker _worker = new();

    /// <summary>Call when the instance list changes (scene swap/live reload): forces a
    /// rebuild and rejects any in-flight background build. The SDF lowerings themselves
    /// are cached on the Parts and deliberately NOT dropped — a reload brings fresh
    /// parts anyway, and an unchanged part keeps its (possibly very expensive) field.</summary>
    public void Invalidate()
    {
        _dirty = true;
        _reportedFailures.Clear();
        _worker.Invalidate();
    }

    /// <summary>
    /// Draws the isolines for every active section plane, scheduling a background
    /// rebuild when stale (or rebuilding inline when <paramref name="requestRender"/>
    /// is null — the offscreen one-shot). Each plane's contours are drawn clipped by
    /// its SIBLING planes (<see cref="SectionClip.Siblings"/> — that method documents
    /// and owns the rule), so a quarter cut shows each cut face's isolines only where
    /// that face is actually exposed instead of across the plane's full extent; the
    /// sibling set comes from the planes the drawn geometry was BUILT for, so stale
    /// contours awaiting a background rebuild stay self-consistent. The plane
    /// comparisons for staleness are deliberate exact equality — change detection,
    /// not geometry.
    /// </summary>
    public void Draw(
        GL gl, IReadOnlyList<PartInstance> instances, IReadOnlyList<bool> visible,
        IReadOnlyList<SectionPlane> planes, SectionCombine combine,
        uint lineProgram, int uModel, int uColor, in SectionUniforms section,
        Span<float> matrix, Action<string> report, Action? requestRender = null)
    {
        if (NeedsRebuild(instances, planes, visible))
        {
            if (requestRender is null)
                BuildInline(gl, instances, visible, planes, report);
            else
                _worker.EnsureBuilding(instances, visible, planes, requestRender);
        }
        if (requestRender is not null && _worker.TryAdopt() is { } build)
            Adopt(gl, build, report);

        gl.UseProgram(lineProgram);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uModel, 1, false, matrix);

        for (int i = 0; i < _geometries.Count; i++)
        {
            if (_geometries[i].PartCount == 0)
                continue;

            SectionClip.Siblings(_builtPlanes, i, combine, _siblings);
            section.Write(gl, _siblings, SectionCombine.Union);

            // d = 0 bright gold (the exact cross-section), positive levels cool,
            // negative (inside material) warm — draw signed families first so the
            // zero contour wins any overdraw. The colours are the shared
            // SectionContours palette, never re-typed here.
            var buffers = _buffers[i];
            var geometry = _geometries[i];
            DrawBatch(gl, buffers.PositiveVao, geometry.PositiveVertices.Length / 3, uColor,
                SectionContours.PositiveColor);
            DrawBatch(gl, buffers.NegativeVao, geometry.NegativeVertices.Length / 3, uColor,
                SectionContours.NegativeColor);
            DrawBatch(gl, buffers.ZeroVao, geometry.ZeroVertices.Length / 3, uColor,
                SectionContours.ZeroColor);
        }
    }

    /// <summary>Synchronous rebuild for the offscreen one-shot path (deterministic,
    /// nothing streams): the pre-worker behaviour, verbatim.</summary>
    private void BuildInline(
        GL gl, IReadOnlyList<PartInstance> instances, IReadOnlyList<bool> visible,
        IReadOnlyList<SectionPlane> planes, Action<string> report)
    {
        var geometries = new List<SectionContourGeometry>(planes.Count);
        var failures = new List<(Part, string)>();
        foreach (var plane in planes)
        {
            geometries.Add(SectionContours.Build(
                instances, visible,
                SectionContours.PlaneFrame(plane.Normal.Normalized(), plane.Offset),
                (part, message) => failures.Add((part, message))));
        }
        Adopt(gl, new SectionContourBuild(
            0, geometries, failures, [.. planes], [.. visible],
            SectionContourWorker.ClippedBits(instances)), report);
    }

    /// <summary>Installs a completed build as the drawn state: geometry, GPU buffers,
    /// the built-target snapshot the staleness check compares against, and the
    /// deduped failure/summary status lines (reported here, on the render thread,
    /// never from the worker).</summary>
    private void Adopt(GL gl, SectionContourBuild build, Action<string> report)
    {
        _geometries.Clear();
        _geometries.AddRange(build.Geometries);
        _dirty = false;
        _builtPlanes.Clear();
        _builtPlanes.AddRange(build.Planes);
        SnapshotVisibility(build.Visible);
        _builtClipped = build.Clipped;
        Upload(gl);
        foreach (var (part, message) in build.Failures)
        {
            if (_reportedFailures.Add(part))
                report(message);
        }
        int most = 0;
        double spacing = 0;
        foreach (var geometry in build.Geometries)
        {
            if (geometry.PartCount > most)
            {
                most = geometry.PartCount;
                spacing = geometry.Spacing;
            }
        }
        if (most != _reportedParts)
        {
            _reportedParts = most;
            if (most > 0)
                report($"section isolines: {most} part(s), spacing {spacing:G3}");
        }
    }

    /// <summary>Deletes the GPU buffers (viewport deinit; GL context current).</summary>
    public void Release(GL gl)
    {
        foreach (var b in _buffers)
        {
            gl.DeleteBuffer(b.ZeroVbo);
            gl.DeleteVertexArray(b.ZeroVao);
            gl.DeleteBuffer(b.PositiveVbo);
            gl.DeleteVertexArray(b.PositiveVao);
            gl.DeleteBuffer(b.NegativeVbo);
            gl.DeleteVertexArray(b.NegativeVao);
        }
        _buffers.Clear();
    }

    private bool NeedsRebuild(
        IReadOnlyList<PartInstance> instances, IReadOnlyList<SectionPlane> planes,
        IReadOnlyList<bool> visible)
    {
        if (_dirty || planes.Count != _builtPlanes.Count || visible.Count != _builtVisibleCount
            || instances.Count != _builtClipped.Length)
            return true;
        for (int i = 0; i < planes.Count; i++)
        {
            if (planes[i] != _builtPlanes[i])
                return true;
        }
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i] != _builtVisible[i])
                return true;
        }
        for (int i = 0; i < instances.Count; i++)
        {
            // ClippedBySection is part of the target: an exempt part contributes no
            // isolines, and the flag can be toggled live from the model tree.
            if (instances[i].Part.ClippedBySection != _builtClipped[i])
                return true;
        }
        return false;
    }

    private void SnapshotVisibility(IReadOnlyList<bool> visible)
    {
        if (_builtVisible.Length < visible.Count)
            _builtVisible = new bool[visible.Count];
        for (int i = 0; i < visible.Count; i++)
            _builtVisible[i] = visible[i];
        _builtVisibleCount = visible.Count;
    }

    private void Upload(GL gl)
    {
        Release(gl);
        foreach (var geometry in _geometries)
        {
            if (geometry.PartCount == 0)
            {
                _buffers.Add(default);
                continue;
            }
            var (zeroVao, zeroVbo) = RenderUploads.UploadLines(gl, geometry.ZeroVertices);
            var (positiveVao, positiveVbo) = RenderUploads.UploadLines(gl, geometry.PositiveVertices);
            var (negativeVao, negativeVbo) = RenderUploads.UploadLines(gl, geometry.NegativeVertices);
            _buffers.Add((zeroVao, zeroVbo, positiveVao, positiveVbo, negativeVao, negativeVbo));
        }
    }

    private static void DrawBatch(
        GL gl, uint vao, int vertexCount, int uColor, (float R, float G, float B) color)
    {
        if (vertexCount == 0)
            return;
        gl.Uniform3(uColor, color.R, color.G, color.B);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)vertexCount);
    }
}
