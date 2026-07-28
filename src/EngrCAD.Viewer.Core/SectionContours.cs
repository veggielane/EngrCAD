using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Interop;
using EngrCAD.Modeling;

namespace EngrCAD.Viewer;

// SDF isolines on the section plane, the pure half: when a section plane cuts a part
// whose geometry is an Sdf (or a Shape with an implicit lowering), iso-distance
// contours of the field are overlaid on the cut — d = 0 is the exact surface
// cross-section, the d = +/-k*spacing family visualizes the field (wall thickness at
// a glance, blend/offset debugging). No GL in this file, which is why it lives in
// EngrCAD.Viewer.Core: the desktop SectionContourRenderer (EngrCAD.Viewer) and the
// browser client both build their overlay here, colours included.

/// <summary>
/// CPU result of one section-contour build: line-program vertex arrays (xyz per
/// vertex, two vertices per segment, world space) split by level sign, plus the level
/// spacing used and how many parts contributed. Immutable; rebuilt only when the
/// section plane or scene changes, never per frame.
/// </summary>
/// <param name="ZeroVertices">The d = 0 contour (the exact surface cross-section).</param>
/// <param name="PositiveVertices">Positive iso levels (outside the material).</param>
/// <param name="NegativeVertices">Negative iso levels (inside the material).</param>
/// <param name="Spacing">Level spacing (1-2-5 rounded).</param>
/// <param name="PartCount">How many parts contributed contours.</param>
public sealed record SectionContourGeometry(
    float[] ZeroVertices, float[] PositiveVertices, float[] NegativeVertices,
    double Spacing, int PartCount)
{
    /// <summary>No contours (no SDF-routed parts under the plane).</summary>
    public static readonly SectionContourGeometry Empty = new([], [], [], 0, 0);
}

/// <summary>Pure geometry for section-plane SDF isolines (no GL — unit-testable, and
/// shared by the desktop renderer and the browser client).</summary>
public static class SectionContours
{
    /// <summary>Iso levels drawn on each side of d = 0 (levels = k·spacing, |k| ≤ this).</summary>
    public const int LevelsPerSide = 4;

    /// <summary>Target marching-squares cells across the longer grid side.</summary>
    public const int TargetCellsAcross = 160;

    /// <summary>Hard cap on samples per grid axis (keeps a nudge recompute bounded).</summary>
    public const int MaxSamplesPerAxis = 256;

    /// <summary>The d = 0 family's colour: bright gold, the exact cross-section.
    /// One definition serves every front end — these three colours are what "the
    /// isoline overlay" looks like, so they live beside the geometry.</summary>
    public static readonly (float R, float G, float B) ZeroColor = (1.00f, 0.90f, 0.45f);

    /// <summary>Positive (outside material) family: cool blue.</summary>
    public static readonly (float R, float G, float B) PositiveColor = (0.42f, 0.66f, 0.90f);

    /// <summary>Negative (inside material) family: warm orange.</summary>
    public static readonly (float R, float G, float B) NegativeColor = (0.92f, 0.55f, 0.32f);

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
    /// The SDF route for a part: <see cref="Part.TryGetSdf"/>, which lowers a
    /// <see cref="Shape"/> to an <see cref="Sdf"/> <b>at most once per part</b> and
    /// caches the result (and any failure) beside the B-Rep lowering
    /// <see cref="Part.TryGetSolid"/> caches. The cache therefore belongs to the
    /// GEOMETRY, not to a renderer: a section toggled off and on, a tab revisit or a
    /// visibility change no longer re-lowers, which matters because a bridged shape's
    /// implicit lowering can build a MeshSdf. Parts with no implicit route (raw
    /// B-Rep/mesh) simply get no isolines; a lowering that FAILED reports through
    /// <paramref name="report"/> so a silently isoline-less part stays diagnosable (the
    /// caller dedupes, since the failure itself is now cached for the part's lifetime).
    /// </summary>
    public static Sdf? SdfRoute(Part part, Action<Part, string>? report = null)
    {
        if (part.TryGetSdf(out var sdf, out string? error))
            return sdf;
        if (error is not null)
            report?.Invoke(part, $"section isolines: {error}");
        return null;
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
        in Frame3d plane, Action<Part, string>? report = null)
    {
        // Candidates: visible instances with an SDF route; their union bounds set the
        // level spacing so all parts share one legend. This first pass performs (or
        // reuses) each part's cached lowering, so failures surface here.
        var bounds = Aabb.Empty;
        for (int i = 0; i < instances.Count; i++)
        {
            if (visible[i] && instances[i].Part.ClippedBySection
                && SdfRoute(instances[i].Part, report) is not null)
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
            // A part exempt from sectioning has no cut face, so it has nothing to draw
            // isolines on (Part.ClippedBySection).
            if (!visible[i] || !instances[i].Part.ClippedBySection
                || SdfRoute(instances[i].Part) is not { } sdf)
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
