using System.Runtime.CompilerServices;
using EngrCAD.Core;
using EngrCAD.Core.Spatial;
using EngrCAD.Mesh;

namespace EngrCAD.Viewer;

// Baked (object-space) ambient occlusion: for every mesh vertex, cast a small
// deterministic cosine-weighted hemisphere of rays against the part's own triangles
// and store the open-sky fraction as a vertex attribute the mesh shader multiplies
// into the shaded color. Crevices, bores, pockets, fillet roots and the inside of
// ribs go darker, which is exactly the depth cue a flat directional light cannot give.
//
// WHY BAKED AND NOT SSAO: the offscreen renderer must match the window byte for byte
// (the whole reason RenderCore.cs exists). Baked AO is vertex DATA computed on the
// CPU, so both passes upload the same floats and shade identically by construction —
// no FBO, no depth/normal prepass, no blur kernel that would resolve differently at
// the offscreen pass's 2x supersampled size, and nothing that interacts with the
// section-plane discard, translucency ordering, edge overlays, or the always-on-top
// annotation layer. It is also deterministic (a fixed direction set, no random
// jitter), which keeps the committed docs PNGs reproducible.
//
// Limitation (documented, v1): occlusion is per part, in the part's own space —
// instances of a part share one bake and no part shadows another. Scene-wide AO
// would have to be rebaked whenever anything moves and could not be shared between
// instances; per-part self-occlusion carries the CAD-relevant signal.

/// <summary>
/// Per-vertex ambient-occlusion baking for the mesh shader's <c>aOcclusion</c>
/// attribute. Pure CPU geometry (no GL), deterministic, and cached per source mesh so
/// the window and the headless pass compute it once and shade from identical data.
/// </summary>
internal static class AmbientOcclusion
{
    /// <summary>Hemisphere rays per vertex. 16 cosine-weighted directions is smooth
    /// enough for a per-vertex signal that is then interpolated across triangles
    /// (visually indistinguishable from 32 on CAD parts, at half the bake cost).</summary>
    public const int DefaultRays = 16;

    /// <summary>
    /// How far the shader mixes the baked occlusion in (the <c>uAmbientOcclusion</c>
    /// uniform when AO is on; 0 is off). Deliberately half, not full: the occlusion
    /// lives on mesh VERTICES and interpolates linearly across triangles, so wherever
    /// neighbouring vertices differ the triangulation itself is what varies across the
    /// face. The crease-aware grouping in <see cref="Bake"/> removes the big offender
    /// (hole rims dragging their whole face down), and half strength keeps whatever
    /// remains — long earcut ears, coarse fans — below the noise of the shading, while
    /// the crevice cue still reads clearly.
    /// </summary>
    public const float Strength = 0.5f;

    /// <summary>
    /// Occlusion search radius as a fraction of the mesh's bounding-box diagonal —
    /// scale-free, so a 10 mm bracket and a 10 m frame get the same look. Hits beyond
    /// it do not darken at all, which keeps AO a local crevice cue instead of a
    /// whole-body dimming.
    /// </summary>
    public const double DefaultRadiusFraction = 0.15;

    /// <summary>
    /// Ray budget for one bake. Above it the per-vertex ray count is halved (never
    /// below <see cref="MinRays"/>) so a 200k-vertex lattice costs about as much as a
    /// 20k-vertex bracket. The rule depends only on the vertex count, so it stays
    /// deterministic and identical in both render passes.
    /// </summary>
    public const long RayBudget = 2_000_000;

    /// <summary>Floor for the adaptive ray count (see <see cref="RayBudget"/>).</summary>
    public const int MinRays = 8;

    /// <summary>
    /// Meshes with more triangles than this are left unoccluded. The ray budget bounds
    /// the ray COUNT, but not the cost of a ray: in lattice-like geometry every ray
    /// walks a labyrinth instead of escaping, and the per-ray cost climbs by more than
    /// an order of magnitude (measured ~10 s for a 100k-triangle gyroid sphere versus
    /// ~8 ms for a 300-triangle bracket). A one-off ten-second stall before the window
    /// appears is a worse trade than flat shading on geometry that already reads as
    /// complex, so dense meshes opt out. The rule is a pure function of the mesh, so it
    /// is deterministic and identical in both render passes.
    /// </summary>
    public const int MaxTriangles = 80_000;

    /// <summary>
    /// Crease threshold for the smoothing groups the bake samples per vertex, as a
    /// cosine (50 degrees). Facets meeting at a gentler angle than this — the steps of
    /// any tessellated curve — share one averaged hemisphere; anything sharper (a box
    /// corner, a bore rim, a pocket wall) is sampled separately per side.
    /// </summary>
    public const double CreaseAngleCosine = 0.6428;   // cos(50 degrees)

    // Cached per source mesh: display meshes are cached on Part, so a scene that is
    // re-rendered (tab switches, offscreen tests, hot reloads of untouched parts)
    // bakes once. ConditionalWeakTable keeps nothing alive on its own.
    private static readonly ConditionalWeakTable<HalfEdgeMesh, float[]> Cache = new();

    /// <summary>
    /// The baked occlusion for a part's display mesh, one float per
    /// <paramref name="render"/> vertex, cached by <paramref name="source"/>.
    /// <paramref name="render"/> must be <see cref="RenderMesh.CreateFlat"/> of
    /// <paramref name="source"/> (both passes build it that way), which makes the
    /// vertex ordering a deterministic function of the source mesh.
    /// </summary>
    public static float[] For(HalfEdgeMesh source, RenderMesh render) =>
        Cache.GetValue(source, _ => Bake(render));

    /// <summary>
    /// Bakes and caches the occlusion of every distinct part up front. The entry points
    /// call this on the same worker thread that runs <c>Scene.PreMesh</c>, so the bake
    /// never lands on the render thread (a scene load or a hot reload would otherwise
    /// stall the first frame by the whole bake time).
    /// <para>Parts are baked in parallel, like <c>Scene.PreMesh</c> and for the same
    /// reason: a bake reads one part's mesh and writes one cache entry keyed by that
    /// mesh, so parts do not interact. <see cref="Bake"/> is itself block-parallel over
    /// vertex groups, so a single dense part still uses the whole machine — but a scene
    /// of many medium parts (the common case) used to run them strictly one at a time.
    /// The result is unchanged whatever the scheduling: <see cref="Bake"/> is a pure
    /// deterministic function of the render mesh, and <see cref="ConditionalWeakTable"/>
    /// is thread-safe (two threads racing on one shared mesh would compute the same
    /// array anyway).</para>
    /// </summary>
    public static void Prime(IEnumerable<EngrCAD.Modeling.Part> parts)
    {
        var distinct = parts as IReadOnlyList<EngrCAD.Modeling.Part> ?? [.. parts];
        ParallelFor.Blocks(0, distinct.Count, (start, end) =>
        {
            for (int i = start; i < end; i++)
            {
                var mesh = distinct[i].GetMesh();
                For(mesh, RenderMesh.CreateFlat(mesh));
            }
        });
    }

    /// <summary>
    /// Bakes per-vertex occlusion in [0, 1] (1 = fully open sky, 0 = fully enclosed)
    /// for a render mesh, by ray casting against the mesh's own triangles.
    /// </summary>
    /// <remarks>
    /// Vertices are grouped by position AND by smoothing group (a flat render mesh
    /// repeats every position once per incident triangle): incident facet normals
    /// within <see cref="CreaseAngleCosine"/> of the first one seen share a group and
    /// average their normals, normals across a sharper crease start a new one. Both
    /// halves matter. Averaging across smooth facets keeps the result continuous, so a
    /// tessellated cylinder does not band. NOT averaging across a crease is what keeps
    /// a hole rim honest: the top face's copy of a rim vertex faces up and is barely
    /// occluded, while the bore wall's copy faces into the hole and is — averaging the
    /// two would drag the whole surrounding face down and paint its triangulation
    /// across it as streaks. The position comparison is deliberate exact equality, not
    /// a tolerance: <see cref="RenderMesh.CreateFlat"/> copies the same vertex position
    /// into every incident triangle, so shared corners are bit-identical by
    /// construction.
    /// </remarks>
    public static float[] Bake(RenderMesh render, int rays = DefaultRays,
        double radiusFraction = DefaultRadiusFraction)
    {
        ArgumentNullException.ThrowIfNull(render);
        int vertexCount = render.VertexCount;
        var occlusion = new float[vertexCount];
        Array.Fill(occlusion, 1f);
        if (vertexCount == 0 || render.TriangleCount == 0 || render.TriangleCount > MaxTriangles)
            return occlusion;

        // Group by position x smoothing group; each group's summed facet normal is the
        // hemisphere axis (see the remarks above for why both keys matter).
        var groupOf = new int[vertexCount];
        var index = new Dictionary<(float X, float Y, float Z), List<int>>(vertexCount);
        var positions = new List<Vector3d>();
        var normals = new List<Vector3d>();
        var seedNormals = new List<Vector3d>();
        // Per POSITION (across all its smoothing groups): the direction that leaves
        // every incident face at once, used only to lift ray origins off the surface.
        var liftOf = new Dictionary<(float X, float Y, float Z), Vector3d>(vertexCount);
        for (int v = 0; v < vertexCount; v++)
        {
            var key = (render.Positions[v * 3], render.Positions[v * 3 + 1], render.Positions[v * 3 + 2]);
            var facet = new Vector3d(
                render.Normals[v * 3], render.Normals[v * 3 + 1], render.Normals[v * 3 + 2]);
            double facetLength = facet.Length;
            var facetUnit = facetLength == 0 ? Vector3d.Zero : facet / facetLength;

            if (!index.TryGetValue(key, out var atPosition))
            {
                atPosition = [];
                index[key] = atPosition;
            }
            liftOf[key] = liftOf.TryGetValue(key, out var accumulated) ? accumulated + facetUnit : facetUnit;
            int group = -1;
            foreach (int candidate in atPosition)
            {
                // Compared against the group's SEED normal, not its running average, so
                // membership never depends on the order within a group.
                if (facetUnit.Dot(seedNormals[candidate]) >= CreaseAngleCosine)
                {
                    group = candidate;
                    break;
                }
            }
            if (group < 0)
            {
                group = positions.Count;
                atPosition.Add(group);
                positions.Add(new Vector3d(key.Item1, key.Item2, key.Item3));
                normals.Add(Vector3d.Zero);
                seedNormals.Add(facetUnit);
            }
            groupOf[v] = group;
            normals[group] += facet;
        }

        var boxes = new Aabb[render.TriangleCount];
        for (int t = 0; t < render.TriangleCount; t++)
            boxes[t] = Aabb.FromPoints([Vertex(render, t, 0), Vertex(render, t, 1), Vertex(render, t, 2)]);
        var bvh = Bvh.Build(boxes);

        var bounds = bvh.Bounds;
        double diagonal = bounds.IsEmpty ? 0 : bounds.Size.Length;
        // Exact-zero guard, not a tolerance: a degenerate (single-point) mesh has no
        // scale to derive a radius from and simply gets no occlusion.
        if (diagonal == 0)
            return occlusion;
        double radius = diagonal * radiusFraction;
        // Lift ray origins off the surface far enough to clear float position
        // quantization (positions are floats) yet far below anything visible.
        double lift = diagonal * 1e-5;

        int groups = positions.Count;
        var directions = HemisphereDirections(RayCount(rays, groups));
        var groupOcclusion = new float[groups];
        var positionArray = positions.ToArray();
        var normalArray = normals.ToArray();
        var liftArray = new Vector3d[groups];
        for (int g = 0; g < groups; g++)
        {
            var p = positionArray[g];
            var direction = liftOf[((float)p.X, (float)p.Y, (float)p.Z)];
            double length = direction.Length;
            // Exact-zero fallback (incident normals that cancel exactly): lift along the
            // group's own normal instead.
            liftArray[g] = length == 0 ? normalArray[g] : direction / length;
        }

        // Each index writes only its own slot (ParallelFor.Blocks' determinism
        // contract), so the result is bit-identical regardless of scheduling.
        ParallelFor.Blocks(0, groups, (start, end) =>
        {
            var stack = new Bvh.NodeView[64];
            for (int g = start; g < end; g++)
            {
                groupOcclusion[g] = Visibility(
                    bvh, render, positionArray[g], normalArray[g], liftArray[g],
                    directions, radius, lift, stack);
            }
        }, minBlockSize: 64);

        for (int v = 0; v < vertexCount; v++)
            occlusion[v] = groupOcclusion[groupOf[v]];
        return occlusion;
    }

    /// <summary>Rays per vertex under <see cref="RayBudget"/>: the requested count,
    /// halved until the whole bake fits (floored at <see cref="MinRays"/>).</summary>
    public static int RayCount(int requested, int vertexGroups)
    {
        int count = Math.Max(1, requested);
        while (count > MinRays && (long)count * vertexGroups > RayBudget)
            count /= 2;
        return Math.Max(MinRays, count);
    }

    /// <summary>Open-sky fraction at one vertex: the share of hemisphere rays that
    /// escape, with hits attenuated linearly to nothing at the search radius.</summary>
    private static float Visibility(
        Bvh bvh, RenderMesh render, in Vector3d position, in Vector3d normalSum,
        in Vector3d liftDirection, Vector3d[] directions, double radius, double lift,
        Bvh.NodeView[] stack)
    {
        double length = normalSum.Length;
        // Exact-zero guard (a position whose incident facet normals cancel exactly, e.g.
        // a zero-thickness sheet): no meaningful hemisphere, so leave it unoccluded.
        if (length == 0)
            return 1f;
        var normal = normalSum / length;
        // Lift along the direction that leaves EVERY incident face, not along this
        // group's normal: at a concave corner (a pocket floor meeting its wall) the
        // group normal runs parallel to the neighbouring face, leaving the origin
        // exactly in its plane, where the ray-triangle t is 0 and the wall that
        // physically blocks half the hemisphere registers as no occlusion at all.
        var frame = Frame3d.FromNormal(position + liftDirection * lift, normal);

        double occluded = 0;
        foreach (var local in directions)
        {
            // Direction scaled by the radius, so a hit's t is already the normalized
            // distance and the attenuation is 1 - t.
            var ray = new Ray3d(frame.Origin, frame.ToWorldVector(local) * radius);
            double nearest = NearestHit(bvh, render, ray, stack);
            if (nearest < 1)
                occluded += 1 - nearest;
        }
        return (float)Math.Clamp(1 - occluded / directions.Length, 0, 1);
    }

    /// <summary>
    /// Nearest triangle hit along the ray SEGMENT (t in (0, 1]), or 1 when the ray
    /// escapes. Own bounded traversal over <see cref="Bvh.NodeView"/> rather than
    /// <c>Bvh.Query(ray, ...)</c>: that one walks an INFINITE ray and hands back every
    /// far-away candidate, which on a dense mesh costs hundreds of triangle tests per
    /// short AO ray (measured 21 s for a 200k-triangle gyroid bake). Pruning on the
    /// slab test's entry parameter against the nearest hit so far keeps the walk local.
    /// </summary>
    private static double NearestHit(Bvh bvh, RenderMesh render, in Ray3d ray, Bvh.NodeView[] stack)
    {
        double nearest = 1.0;
        int top = 0;
        stack[top++] = bvh.Root;
        while (top > 0)
        {
            var node = stack[--top];
            if (!ray.Intersects(node.Bounds, out double entry, out _) || entry >= nearest)
                continue;
            if (node.IsLeaf)
            {
                foreach (int triangle in node.Items)
                {
                    if (RayTriangle(ray, Vertex(render, triangle, 0), Vertex(render, triangle, 1),
                            Vertex(render, triangle, 2), out double t) && t < nearest)
                        nearest = t;
                }
            }
            else
            {
                // The BVH is median-split and shallow; 64 slots is the same depth budget
                // Bvh's own traversals stackalloc.
                stack[top++] = node.Left;
                stack[top++] = node.Right;
            }
        }
        return nearest;
    }

    // One deterministic cosine-weighted direction set per ray count, shared by every
    // vertex (rotated into the vertex's own frame). Deterministic beats jittered here:
    // there is no blur pass to hide noise, and identical directions everywhere make the
    // sampling bias smooth instead of speckled.
    private static readonly Dictionary<int, Vector3d[]> DirectionCache = [];

    private static Vector3d[] HemisphereDirections(int rays)
    {
        lock (DirectionCache)
        {
            if (DirectionCache.TryGetValue(rays, out var cached))
                return cached;
            var directions = new Vector3d[rays];
            // Cosine-weighted hemisphere about +Z: u stratified over [0, 1), the polar
            // angle from z = sqrt(1 - u) (so the density follows cos(theta) and the
            // plain average is the correctly weighted estimate), the azimuth stepped by
            // the golden angle for a low-discrepancy spread.
            const double golden = 0.618033988749895;
            for (int i = 0; i < rays; i++)
            {
                double u = (i + 0.5) / rays;
                double r = Math.Sqrt(u);
                double phi = 2 * Math.PI * (i * golden % 1.0);
                directions[i] = new Vector3d(r * Math.Cos(phi), r * Math.Sin(phi), Math.Sqrt(1 - u));
            }
            DirectionCache[rays] = directions;
            return directions;
        }
    }

    private static Vector3d Vertex(RenderMesh mesh, int triangle, int corner)
    {
        uint i = mesh.Indices[triangle * 3 + corner];
        return new Vector3d(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]);
    }

    /// <summary>
    /// Moller-Trumbore any-hit test; t is in units of the (unnormalized) ray direction,
    /// so a hit inside the search radius has t &lt; 1. Deliberately separate from the
    /// picking copy in <see cref="ViewportControl"/>: that one wants the nearest hit and
    /// a world point for the measure tool, this one only asks whether a hemisphere ray
    /// escapes.
    /// </summary>
    private static bool RayTriangle(
        in Ray3d ray, in Vector3d a, in Vector3d b, in Vector3d c, out double t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = ray.Direction.Cross(e2);
        double determinant = e1.Dot(p);
        // Round-off-scale parallel-ray guard (a shading estimate, not model geometry:
        // a missed edge-on triangle costs a fraction of one vertex's occlusion).
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
        return t > 0 && t < 1;
    }
}
