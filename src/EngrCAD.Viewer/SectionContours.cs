using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using GL = Silk.NET.OpenGL.GL;
using Silk.NET.OpenGL;

namespace EngrCAD.Viewer;

// SDF isolines on the section plane: when the section plane cuts a part whose
// geometry is an Sdf (or a Shape with an implicit lowering), iso-distance contours of
// the field are overlaid on the cut — d = 0 is the exact surface cross-section, the
// d = +/-k*spacing family visualizes the field (wall thickness at a glance,
// blend/offset debugging). This file is self-contained: SectionContours is the pure
// CPU geometry (unit-testable, no GL), SectionContourRenderer owns the GL state;
// ViewportControl only calls Invalidate/Draw/Release.

/// <summary>
/// CPU result of one section-contour build: line-program vertex arrays (xyz per
/// vertex, two vertices per segment, world space) split by level sign, plus the level
/// spacing used and how many parts contributed. Immutable; rebuilt only when the
/// section plane or scene changes, never per frame.
/// </summary>
internal sealed record SectionContourGeometry(
    float[] ZeroVertices, float[] PositiveVertices, float[] NegativeVertices,
    double Spacing, int PartCount)
{
    public static readonly SectionContourGeometry Empty = new([], [], [], 0, 0);
}

/// <summary>Pure geometry for section-plane SDF isolines (no GL — unit-testable).</summary>
internal static class SectionContours
{
    /// <summary>Iso levels drawn on each side of d = 0 (levels = k·spacing, |k| ≤ this).</summary>
    public const int LevelsPerSide = 4;

    /// <summary>Target marching-squares cells across the longer grid side.</summary>
    public const int TargetCellsAcross = 160;

    /// <summary>Hard cap on samples per grid axis (keeps a nudge recompute bounded).</summary>
    public const int MaxSamplesPerAxis = 256;

    /// <summary>
    /// The section plane as a frame from its clip rule dot(p, axis) &gt; offset
    /// (axis unit-length; the plane's X/Y span it, Z is the axis). Cardinal axes get
    /// their natural cyclic in-plane frame — the comparisons are exact-equality
    /// *semantic* tests (deliberately not Tolerance): a not-exactly-cardinal axis
    /// simply falls back to the arbitrary-perpendicular convention, which is correct
    /// for any axis.
    /// </summary>
    public static Frame3d PlaneFrame(in Vector3d axis, double offset)
    {
        var origin = axis * offset;
        if (axis == Vector3d.UnitZ)
            return Frame3d.FromOrthonormal(origin, Vector3d.UnitX, Vector3d.UnitY);
        if (axis == Vector3d.UnitX)
            return Frame3d.FromOrthonormal(origin, Vector3d.UnitY, Vector3d.UnitZ);
        if (axis == Vector3d.UnitY)
            return Frame3d.FromOrthonormal(origin, Vector3d.UnitZ, Vector3d.UnitX);
        return Frame3d.FromNormal(origin, axis);
    }

    /// <summary>
    /// The SDF route for a part, cached by part reference: SDF geometry directly,
    /// Shapes through their implicit lowering (done once — lowering a bridged shape
    /// can build a MeshSdf), everything else (raw B-Rep/mesh parts) null = no
    /// isolines. A failed lowering caches null rather than throwing per rebuild.
    /// </summary>
    public static Sdf? SdfRoute(Part part, Dictionary<Part, Sdf?> cache)
    {
        if (cache.TryGetValue(part, out var cached))
            return cached;
        Sdf? sdf = part.Geometry switch
        {
            Sdf direct => direct,
            Shape shape => TryLower(shape),
            _ => null,
        };
        cache[part] = sdf;
        return sdf;

        static Sdf? TryLower(Shape shape)
        {
            try
            {
                return shape.CanConvertTo(TargetRep.Implicit) ? shape.ToImplicit() : null;
            }
            catch
            {
                return null;   // lowering failed — treat as no implicit route
            }
        }
    }

    /// <summary>
    /// Builds the contour line vertices for every visible instance with an SDF route
    /// that the section plane actually cuts. Levels are k·spacing for |k| ≤
    /// <see cref="LevelsPerSide"/>, spacing derived from the candidates' world bounds
    /// (1-2-5 rounded). Per instance the plane is mapped into the part's own space
    /// through the inverse instance transform (an affine map takes the sample
    /// rectangle to a parallelogram, which the extraction handles exactly); level
    /// distances are therefore measured in part-local units — exact for rigid
    /// placements, display-only under scaling. The resulting world-space lines are
    /// pulled 1% of the spacing to the visible side of the clip plane so the
    /// fragment-shader section discard (and float interpolation noise) never eats
    /// them; that offset is invisible at contour scale.
    /// </summary>
    public static SectionContourGeometry Build(
        IReadOnlyList<PartInstance> instances, IReadOnlyList<bool> visible,
        in Frame3d plane, Dictionary<Part, Sdf?> cache)
    {
        // Candidates: visible instances with an SDF route; their union bounds set the
        // level spacing so all parts share one legend.
        var bounds = Aabb.Empty;
        for (int i = 0; i < instances.Count; i++)
        {
            if (visible[i] && SdfRoute(instances[i].Part, cache) is not null)
                bounds = bounds.Union(instances[i].Bounds());
        }
        if (bounds.IsEmpty)
            return SectionContourGeometry.Empty;

        double spacing = RenderGeometry.NiceStep(bounds.Size.Length / 60.0);
        Span<double> levels = stackalloc double[2 * LevelsPerSide + 1];
        for (int k = -LevelsPerSide; k <= LevelsPerSide; k++)
            levels[k + LevelsPerSide] = k * spacing;

        var lift = plane.Z * (spacing * 0.01);
        var zero = new List<float>();
        var positive = new List<float>();
        var negative = new List<float>();
        int partCount = 0;

        for (int i = 0; i < instances.Count; i++)
        {
            if (!visible[i] || SdfRoute(instances[i].Part, cache) is not { } sdf)
                continue;
            var instance = instances[i];
            var world = instance.Bounds();
            if (world.IsEmpty)
                continue;

            // Project the world bounds' corners into plane coordinates (u, v in-plane,
            // w along the axis); skip instances the plane does not cross.
            double uMin = double.PositiveInfinity, uMax = double.NegativeInfinity;
            double vMin = double.PositiveInfinity, vMax = double.NegativeInfinity;
            double wMin = double.PositiveInfinity, wMax = double.NegativeInfinity;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3d(
                    (c & 1) == 0 ? world.Min.X : world.Max.X,
                    (c & 2) == 0 ? world.Min.Y : world.Max.Y,
                    (c & 4) == 0 ? world.Min.Z : world.Max.Z);
                var local = plane.ToLocal(corner);
                uMin = Math.Min(uMin, local.X); uMax = Math.Max(uMax, local.X);
                vMin = Math.Min(vMin, local.Y); vMax = Math.Max(vMax, local.Y);
                wMin = Math.Min(wMin, local.Z); wMax = Math.Max(wMax, local.Z);
            }
            if (wMin > 0 || wMax < 0)
                continue;

            // Pad the sample rectangle so the outermost positive levels (which lie
            // outside the body, hence possibly outside its bounds) are not truncated.
            double pad = (LevelsPerSide + 0.5) * spacing;
            uMin -= pad; uMax += pad;
            vMin -= pad; vMax += pad;
            double du = uMax - uMin;
            double dv = vMax - vMin;
            double cell = Math.Max(du, dv) / TargetCellsAcross;
            int nu = Math.Clamp((int)Math.Ceiling(du / cell) + 1, 2, MaxSamplesPerAxis);
            int nv = Math.Clamp((int)Math.Ceiling(dv / cell) + 1, 2, MaxSamplesPerAxis);

            // Map the world sample rectangle into the part's own space through the
            // inverse instance transform (affine-safe: rectangle -> parallelogram).
            if (!instance.World.TryInvert(out var toLocal))
                continue;
            var rectOrigin = plane.ToWorld(new Vector3d(uMin, vMin, 0));
            var uCornerWorld = plane.ToWorld(new Vector3d(uMax, vMin, 0));
            var vCornerWorld = plane.ToWorld(new Vector3d(uMin, vMax, 0));
            var localOrigin = toLocal.TransformPoint(rectOrigin);
            var localU = toLocal.TransformPoint(uCornerWorld) - localOrigin;
            var localV = toLocal.TransformPoint(vCornerWorld) - localOrigin;

            var contours = SdfContours.OnPlane(sdf, localOrigin, localU, localV, nu, nv, levels);
            partCount++;
            for (int l = 0; l < contours.Count; l++)
            {
                int k = l - LevelsPerSide;
                var target = k == 0 ? zero : k > 0 ? positive : negative;
                foreach (var (a, b) in contours[l].Segments)
                {
                    Append(target, instance.World.TransformPoint(a) - lift);
                    Append(target, instance.World.TransformPoint(b) - lift);
                }
            }
        }

        if (partCount == 0)
            return SectionContourGeometry.Empty;
        return new SectionContourGeometry([.. zero], [.. positive], [.. negative], spacing, partCount);

        static void Append(List<float> target, in Vector3d p)
        {
            target.Add((float)p.X);
            target.Add((float)p.Y);
            target.Add((float)p.Z);
        }
    }
}

/// <summary>
/// GL-side owner of the section-isoline overlay: caches SDF routes and the built
/// geometry, rebuilds only when the section plane moves or the scene/visibility
/// changes (never per frame), and draws three colored line batches through the shared
/// line program. All methods must be called with the GL context current (render pass).
/// </summary>
internal sealed class SectionContourRenderer
{
    private readonly Dictionary<Part, Sdf?> _sdfCache = [];
    private SectionContourGeometry _geometry = SectionContourGeometry.Empty;
    private bool _dirty = true;
    private Vector3d _builtAxis;
    private double _builtOffset = double.NaN;
    private bool[] _builtVisible = [];
    private int _builtVisibleCount;
    private int _reportedParts = -1;

    private bool _uploaded;
    private uint _zeroVao, _zeroVbo, _positiveVao, _positiveVbo, _negativeVao, _negativeVbo;

    /// <summary>Call when the instance list changes (scene swap/live reload): drops the
    /// per-part SDF cache (parts are fresh objects after a reload) and forces a rebuild.</summary>
    public void Invalidate()
    {
        _dirty = true;
        _sdfCache.Clear();
    }

    /// <summary>
    /// Draws the isolines for the current section plane, rebuilding geometry and GPU
    /// buffers first when stale. The plane arrives as its clip rule (axis, offset);
    /// the caller passes the line program and its uModel/uColor locations (view/proj
    /// uniforms are already set for the frame). The axis/offset comparisons for
    /// staleness are deliberate exact equality — change detection, not geometry (and
    /// the axis must be part of the key: switching axes can leave the offset
    /// numerically identical, e.g. 0 for an origin-centered scene).
    /// </summary>
    public void Draw(
        GL gl, IReadOnlyList<PartInstance> instances, IReadOnlyList<bool> visible,
        in Vector3d axis, double offset,
        uint lineProgram, int uModel, int uColor, Span<float> matrix, Action<string> report)
    {
        if (NeedsRebuild(axis, offset, visible))
        {
            _geometry = SectionContours.Build(
                instances, visible, SectionContours.PlaneFrame(axis, offset), _sdfCache);
            _dirty = false;
            _builtAxis = axis;
            _builtOffset = offset;
            SnapshotVisibility(visible);
            Upload(gl);
            if (_geometry.PartCount != _reportedParts)
            {
                _reportedParts = _geometry.PartCount;
                if (_geometry.PartCount > 0)
                    report($"section isolines: {_geometry.PartCount} part(s), spacing {_geometry.Spacing:G3}");
            }
        }
        if (_geometry.PartCount == 0)
            return;

        gl.UseProgram(lineProgram);
        CameraMath.WriteColumnMajor(Matrix4d.Identity, matrix);
        gl.UniformMatrix4(uModel, 1, false, matrix);

        // d = 0 bright gold (the exact cross-section), positive levels cool,
        // negative (inside material) warm — draw signed families first so the
        // zero contour wins any overdraw.
        DrawBatch(gl, _positiveVao, _geometry.PositiveVertices.Length / 3, uColor, 0.42f, 0.66f, 0.90f);
        DrawBatch(gl, _negativeVao, _geometry.NegativeVertices.Length / 3, uColor, 0.92f, 0.55f, 0.32f);
        DrawBatch(gl, _zeroVao, _geometry.ZeroVertices.Length / 3, uColor, 1.00f, 0.90f, 0.45f);
    }

    /// <summary>Deletes the GPU buffers (viewport deinit; GL context current).</summary>
    public void Release(GL gl)
    {
        if (!_uploaded)
            return;
        gl.DeleteBuffer(_zeroVbo);
        gl.DeleteVertexArray(_zeroVao);
        gl.DeleteBuffer(_positiveVbo);
        gl.DeleteVertexArray(_positiveVao);
        gl.DeleteBuffer(_negativeVbo);
        gl.DeleteVertexArray(_negativeVao);
        _uploaded = false;
    }

    private bool NeedsRebuild(in Vector3d axis, double offset, IReadOnlyList<bool> visible)
    {
        if (_dirty || axis != _builtAxis || offset != _builtOffset || visible.Count != _builtVisibleCount)
            return true;
        for (int i = 0; i < visible.Count; i++)
        {
            if (visible[i] != _builtVisible[i])
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
        if (_geometry.PartCount == 0)
            return;
        (_zeroVao, _zeroVbo) = RenderGeometry.UploadLines(gl, _geometry.ZeroVertices);
        (_positiveVao, _positiveVbo) = RenderGeometry.UploadLines(gl, _geometry.PositiveVertices);
        (_negativeVao, _negativeVbo) = RenderGeometry.UploadLines(gl, _geometry.NegativeVertices);
        _uploaded = true;
    }

    private static void DrawBatch(GL gl, uint vao, int vertexCount, int uColor, float r, float g, float b)
    {
        if (vertexCount == 0)
            return;
        gl.Uniform3(uColor, r, g, b);
        gl.BindVertexArray(vao);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)vertexCount);
    }
}
