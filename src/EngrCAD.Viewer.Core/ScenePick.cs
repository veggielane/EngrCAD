using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Viewer;

/// <summary>
/// One distinct part's pick geometry: its display mesh plus a triangle BVH over it.
/// Built once per part and shared by every instance of it, exactly as the GPU buffers
/// are — N occurrences of a bolt cost one BVH and N matrices.
/// <para>
/// The ray is transformed into the instance's LOCAL space rather than the mesh into
/// world space, which is what makes that sharing possible at all.
/// </para>
/// </summary>
public sealed class PickMesh
{
    private PickMesh(RenderMesh mesh, Bvh bvh)
    {
        Mesh = mesh;
        Bvh = bvh;
    }

    /// <summary>The display mesh the BVH indexes (positions are read straight out of it).</summary>
    public RenderMesh Mesh { get; }

    /// <summary>Triangle BVH: leaf items are triangle indices into <see cref="Mesh"/>.</summary>
    public Bvh Bvh { get; }

    /// <summary>Builds the triangle BVH for a display mesh.</summary>
    public static PickMesh Build(RenderMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var boxes = new Aabb[mesh.TriangleCount];
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            boxes[t] = Aabb.FromPoints(
            [
                Vertex(mesh, mesh.Indices[t * 3]),
                Vertex(mesh, mesh.Indices[t * 3 + 1]),
                Vertex(mesh, mesh.Indices[t * 3 + 2]),
            ]);
        }
        return new PickMesh(mesh, Bvh.Build(boxes));
    }

    internal static Vector3d Vertex(RenderMesh mesh, uint index) => new(
        mesh.Positions[index * 3],
        mesh.Positions[index * 3 + 1],
        mesh.Positions[index * 3 + 2]);
}

/// <summary>
/// One pickable instance: shared pick geometry, its pose, and the two facts that decide
/// whether a ray may land on it at all.
/// </summary>
/// <param name="Mesh">Pick geometry (shared between instances of one part).</param>
/// <param name="Model">Instance transform (frame chain x part transform).</param>
/// <param name="Visible">Hidden instances are not pickable — what the tree's checkbox
/// means, and the reason a hidden part cannot be clicked through a neighbour.</param>
/// <param name="ClippedBySection">Mirrors <c>Part.ClippedBySection</c>: false means the
/// section planes never remove this instance, so picking never skips it either (the
/// drafting convention that a bolt is drawn unsectioned inside a cutaway).</param>
public readonly record struct PickInstance(
    PickMesh Mesh, Matrix4d Model, bool Visible = true, bool ClippedBySection = true);

/// <summary>
/// What a pick ray found: the index into the instance list, and the world-space point
/// on that instance's surface. <see cref="Index"/> is −1 when the ray hit nothing, in
/// which case <see cref="World"/> is meaningless.
/// </summary>
/// <param name="Index">Instance index, or −1.</param>
/// <param name="World">World-space point on the picked surface.</param>
public readonly record struct PickResult(int Index, Vector3d World)
{
    /// <summary>Nothing under the cursor.</summary>
    public static readonly PickResult None = new(-1, default);

    /// <summary>Whether the ray hit anything.</summary>
    public bool Hit => Index >= 0;
}

/// <summary>
/// Click picking and hover, in ONE place: unproject the pixel to a world ray, test it
/// against each visible instance's triangle BVH with Möller–Trumbore, and keep the
/// nearest hit the section planes do not remove.
/// <para>
/// Shared because every front end owes its user the same answer. The desktop viewport
/// and the browser client both call this: a picker that re-derived its own ray
/// unprojection would disagree with the camera it was drawn with, and the disagreement
/// would be invisible until someone clicked near an edge. That is the same failure mode
/// <see cref="CameraMath"/> exists to prevent, one layer up.
/// </para>
/// <para>
/// Section awareness is built in rather than bolted on later, for the reason
/// <see cref="SectionClip"/> exists: the shaders' discard rule and the CPU's must be the
/// same sentence, or picking and rendering disagree about which corner a quarter cut
/// removed. A front end with no section planes passes none and pays nothing.
/// </para>
/// </summary>
public static class ScenePick
{
    /// <summary>
    /// Unprojects a pixel to the world-space segment through the frustum: the points
    /// where the ray crosses the near and far planes.
    /// <para>
    /// Kept separate from <see cref="Nearest"/> so it can be tested on its own, because
    /// this is the step with nothing to check it: a ray that is subtly wrong still
    /// returns a plausible part.
    /// </para>
    /// </summary>
    /// <param name="pixelX">Pixel x, measured from the LEFT of the viewport.</param>
    /// <param name="pixelY">Pixel y, measured from the TOP of the viewport (the
    /// convention of both Avalonia and the DOM; NOT glReadPixels order).</param>
    /// <param name="width">Viewport width in the same pixel units.</param>
    /// <param name="height">Viewport height in the same pixel units.</param>
    /// <param name="viewProjection">projection * view, the matrix the frame was drawn with.</param>
    /// <param name="near">Point where the ray crosses the near plane.</param>
    /// <param name="far">Point where the ray crosses the far plane.</param>
    /// <returns>False when the matrix is singular (nothing has been framed yet).</returns>
    public static bool TryRay(
        double pixelX, double pixelY, double width, double height,
        in Matrix4d viewProjection, out Vector3d near, out Vector3d far)
    {
        near = default;
        far = default;
        if (!viewProjection.TryInvert(out var unproject))
            return false;

        double w = Math.Max(1, width);
        double h = Math.Max(1, height);
        double ndcX = 2 * pixelX / w - 1;
        double ndcY = 1 - 2 * pixelY / h;
        near = unproject.TransformPoint((ndcX, ndcY, -1));
        far = unproject.TransformPoint((ndcX, ndcY, 1));
        return true;
    }

    /// <summary>
    /// The nearest visible instance under a pixel, and the world point on it.
    /// </summary>
    /// <param name="pixelX">Pixel x from the left.</param>
    /// <param name="pixelY">Pixel y from the top.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    /// <param name="viewProjection">projection * view for the frame being picked.</param>
    /// <param name="instances">The instance list; the returned index addresses it.</param>
    /// <param name="sectionEnabled">Whether section planes are active.</param>
    /// <param name="sectionPlanes">The active planes (null or empty = none).</param>
    /// <param name="combine">How several planes combine (see <see cref="SectionClip"/>).</param>
    /// <param name="scratch">Optional reusable BVH candidate list — hover re-picks on
    /// every few pixels of pointer travel, and this keeps that allocation-free.</param>
    public static PickResult Nearest(
        double pixelX, double pixelY, double width, double height,
        in Matrix4d viewProjection,
        IReadOnlyList<PickInstance> instances,
        bool sectionEnabled = false,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine combine = SectionCombine.Intersection,
        List<int>? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (!TryRay(pixelX, pixelY, width, height, viewProjection, out var near, out var far))
            return PickResult.None;
        return Nearest(near, far, instances, sectionEnabled, sectionPlanes, combine, scratch);
    }

    /// <summary>
    /// The nearest visible instance along an explicit world-space segment — the half of
    /// <see cref="Nearest(double, double, double, double, in Matrix4d, IReadOnlyList{PickInstance}, bool, IReadOnlyList{SectionPlane}?, SectionCombine, List{int}?)"/>
    /// that does not need a camera, for callers that already have a ray.
    /// </summary>
    public static PickResult Nearest(
        in Vector3d near, in Vector3d far,
        IReadOnlyList<PickInstance> instances,
        bool sectionEnabled = false,
        IReadOnlyList<SectionPlane>? sectionPlanes = null,
        SectionCombine combine = SectionCombine.Intersection,
        List<int>? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var planes = sectionPlanes ?? [];
        var hits = scratch ?? [];

        int best = -1;
        double bestT = double.PositiveInfinity;
        var bestWorld = Vector3d.Zero;

        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (!instance.Visible || instance.Mesh is null)
                continue;
            if (!instance.Model.TryInvert(out var toLocal))
                continue;

            // The ray goes into the instance's space, never the mesh into world space:
            // that is what lets N occurrences share one BVH.
            var origin = toLocal.TransformPoint(near);
            var direction = toLocal.TransformPoint(far) - origin;
            var ray = new Ray3d(origin, direction);

            hits.Clear();
            instance.Mesh.Bvh.Query(ray, hits);
            var mesh = instance.Mesh.Mesh;
            foreach (int triangle in hits)
            {
                var a = PickMesh.Vertex(mesh, mesh.Indices[triangle * 3]);
                var b = PickMesh.Vertex(mesh, mesh.Indices[triangle * 3 + 1]);
                var c = PickMesh.Vertex(mesh, mesh.Indices[triangle * 3 + 2]);
                if (!RayTriangle(ray, a, b, c, out double t) || t >= bestT)
                    continue;

                // t is in units of the (unnormalized) local direction, which is the
                // same parameter along the world segment: the transform is affine, so
                // comparing t across instances of different scales is still valid.
                var world = instance.Model.TransformPoint(origin + direction * t);
                // A surface the section plane clipped away is not there to click: skip
                // it and keep looking, so the ray lands on the interior the cut exposed
                // rather than the removed shell. A part exempted from sectioning is
                // never clipped, so it is never skipped either.
                if (instance.ClippedBySection
                    && SectionClip.Hides(sectionEnabled, world, planes, combine))
                    continue;

                bestT = t;
                best = i;
                bestWorld = world;
            }
        }

        return best < 0 ? PickResult.None : new PickResult(best, bestWorld);
    }

    /// <summary>Möller–Trumbore; t is in units of the (unnormalized) ray direction.</summary>
    private static bool RayTriangle(
        in Ray3d ray, in Vector3d a, in Vector3d b, in Vector3d c, out double t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = ray.Direction.Cross(e2);
        double determinant = e1.Dot(p);
        // Round-off-scale parallel-ray guard (picking robustness, not model geometry:
        // a missed edge-on triangle costs a click, never a weld).
        if (Math.Abs(determinant) < 1e-15)
            return false;
        double inverse = 1.0 / determinant;
        var s = ray.Origin - a;
        double u = s.Dot(p) * inverse;
        if (u < 0 || u > 1)
            return false;
        var q = s.Cross(e1);
        double v = ray.Direction.Dot(q) * inverse;
        if (v < 0 || u + v > 1)
            return false;
        t = e2.Dot(q) * inverse;
        // Minimum hit distance (direction-length units): rejects self-hits at the ray
        // origin; a UI-picking threshold, not a kernel tolerance.
        return t > 1e-9;
    }
}

/// <summary>
/// Distance-based sampling throttle for the viewport's hover highlight: the hover
/// raycast re-runs only when the pointer has traveled at least the threshold since the
/// last sample, so slow jitter costs nothing and fast sweeps still track. Pure state
/// machine — unit-tested without UI.
/// <para>
/// Shared with the browser client for the same reason <see cref="CameraMath"/>'s drag
/// constants are: how responsive hover feels is a product decision, and a second front
/// end that re-typed the threshold would feel subtly different for no reason anyone
/// could later reconstruct.
/// </para>
/// </summary>
public struct HoverThrottle(double thresholdPixels)
{
    /// <summary>The viewport's threshold: re-pick every 4+ device-independent pixels of
    /// pointer travel.</summary>
    public const double DefaultThreshold = 4.0;

    private readonly double _thresholdSquared = thresholdPixels * thresholdPixels;
    private double _lastX, _lastY;
    private bool _hasSample;

    /// <summary>True when the position is far enough from the last accepted sample
    /// (or no sample exists); accepting records the position.</summary>
    public bool ShouldSample(double x, double y)
    {
        if (_hasSample)
        {
            double dx = x - _lastX, dy = y - _lastY;
            if (dx * dx + dy * dy < _thresholdSquared)
                return false;
        }
        _lastX = x;
        _lastY = y;
        _hasSample = true;
        return true;
    }

    /// <summary>Forgets the last sample so the next move re-picks immediately
    /// (drag ended, scene changed).</summary>
    public void Reset() => _hasSample = false;
}
