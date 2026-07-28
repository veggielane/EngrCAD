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
//
// WHY IT STREAMS IN THE WINDOW: the bake is honest ray casting and it is not cheap
// (measured on the demo scene: 12.3 s total, of which the M8 stud is 3.0 s and the
// tapped block 5.5 s), and it already saturates every core, so more parallelism has
// nothing left to give. Blocking the window on it made opening the demo feel broken.
// Instead the window shows the scene IMMEDIATELY, flat-lit, and each part's occlusion
// arrives as its bake finishes: a mesh VAO with no occlusion buffer reads the constant
// 1.0 (RenderUploads.SetDefaultOcclusion), which is EXACTLY the ambient-occlusion-off
// shading, so an unbaked part is not a placeholder — it is the correct flat-lit render
// of that part, and the only thing that changes when the bake lands is that crevices
// darken. Cheapest part first, so most of a scene lights up in the first frames.
// The headless pass still bakes inline: it is one-shot and must be deterministic, so
// RenderToImage output is unchanged and window/offscreen parity holds once the window
// has converged (a second or two).
//
// WHY THERE IS NO ANY-HIT EARLY-OUT: the obvious way to make a visibility bake cheap is
// to stop traversing at the first intersection, since "is this ray blocked" is a boolean
// question. It is not the question this bake asks. Occlusion accumulates as 1 - t, so a
// hit darkens in proportion to how CLOSE it is and the search radius fades hits out
// rather than cutting them off — which is what keeps AO a local crevice cue instead of a
// whole-body dimming. A boolean bake is the radius-to-infinity limit of this one, and it
// is a different renderer: measured on a pocketed block, the boolean answer is 0.055
// darker over the vertices that see an occluder at all
// (AmbientOcclusionTests.Occlusion_AttenuatesWithDISTANCE_NotJustHitOrMiss pins this so
// the early-out cannot be reintroduced as an "optimization").
//
// What IS exact, and is done: the traversal visits the nearer child box first. The answer
// is a MINIMUM over hits and a subtree is skipped only when the ray's entry parameter into
// its box already exceeds the best hit so far, so ordering cannot change the result — it
// only reaches a tight bound sooner. Note where that can and cannot pay: a ray that
// ESCAPES never sets the bound below 1, so ordering prunes nothing for it. The win is
// therefore proportional to how occluded the geometry is, which the measurements show
// exactly — 1.19x on a gyroid lattice (mean occlusion 0.70) and 1.04x, i.e. nothing, on a
// smooth CSG blob (mean occlusion 0.95).

/// <summary>
/// Per-vertex ambient-occlusion baking for the mesh shader's <c>aOcclusion</c>
/// attribute. Pure CPU geometry (no GL), deterministic, and cached per source mesh so
/// the window and the headless pass compute it once and shade from identical data.
/// </summary>
internal static class AmbientOcclusion
{
    /// <summary>
    /// Hemisphere rays per vertex: a cost/quality point, NOT a converged answer, and the
    /// measurements say so plainly (<c>AmbientOcclusionBenchmark.RayCountTradeoff</c>).
    /// Halving to 8 is 1.6–1.9× faster and moves the per-vertex occlusion by up to
    /// 0.21–0.30 (mean 0.003–0.038); doubling to 32 costs 1.4–1.6× and still moves it by
    /// up to 0.04–0.13. At <see cref="Strength"/> 0.5 a 0.30 swing is 0.15 of shaded
    /// value — about 38 of 255 levels — so this is not dithering noise.
    /// <para><b>The PNG oracle settles it</b>: rebuilding the docs site with 8 rays
    /// changes <b>59 of the 87 rendered images</b>. Reducing the ray count is therefore a
    /// deliberate change to what users see, not a free speedup, and any future attempt to
    /// buy time here has to argue about appearance rather than about performance.</para>
    /// </summary>
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
    /// The cached bake for a mesh, or null if it has not been baked yet — the
    /// NON-blocking lookup. The window uses this everywhere it touches GL, so a bake can
    /// never land on the render thread; a null simply means the part draws flat-lit
    /// until <see cref="BakeInBackground"/> publishes its result.
    /// </summary>
    public static float[]? TryGet(HalfEdgeMesh source) =>
        Cache.TryGetValue(source, out var cached) ? cached : null;

    /// <summary>
    /// Bakes every part's occlusion on a background thread and publishes each result
    /// into the shared cache as it finishes, calling <paramref name="onPartBaked"/> with
    /// the part after each one (the viewport repaints, hosts update per-part progress,
    /// and <see cref="TryGet"/> starts returning that
    /// part's data). Parts are baked CHEAPEST FIRST — sorted by triangle count — so the
    /// bulk of a scene gains its occlusion in the first moments and only the one or two
    /// heavy parts trail; already-cached parts cost nothing and are not counted.
    /// <paramref name="onFinished"/> reports how many parts were actually baked and how
    /// long the job took (nothing is reported when everything was already cached).
    /// Cancellation is checked between parts: <see cref="Bake"/> itself is one parallel
    /// pass, and a scene swap only needs the REST of the queue dropped.
    /// </summary>
    public static void BakeInBackground(
        IReadOnlyList<EngrCAD.Modeling.Part> parts, Action<EngrCAD.Modeling.Part> onPartBaked,
        Action<int, TimeSpan>? onFinished, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(onPartBaked);
        if (parts.Count == 0)
            return;

        Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            int baked = 0;
            // GetMesh is cached (Scene.PreMesh primed it) and lock-protected, so reading
            // it here is cheap and safe alongside the render thread; FaceCount orders the
            // queue without building a RenderMesh for parts we may never reach.
            foreach (var part in parts.OrderBy(p => p.GetMesh().FaceCount))
            {
                if (token.IsCancellationRequested)
                    return;
                var mesh = part.GetMesh();
                if (TryGet(mesh) is not null)
                    continue;
                For(mesh, RenderMesh.CreateFlat(mesh));
                baked++;
                onPartBaked(part);
            }
            if (baked > 0 && !token.IsCancellationRequested)
                onFinished?.Invoke(baked, started.Elapsed);
        }, token);
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

        // Triangles are widened to doubles ONCE, as (corner, edge1, edge2) — the exact form
        // the Moller-Trumbore test wants. The inner loop was re-reading three float triples
        // through the index buffer and re-subtracting them on every ray-triangle test, of
        // which a bake does tens of millions. float→double is exact, so this is a pure
        // restructuring: the arithmetic that follows sees the identical values.
        var triangles = new Vector3d[render.TriangleCount * 3];
        var boxes = new Aabb[render.TriangleCount];
        for (int t = 0; t < render.TriangleCount; t++)
        {
            var a = Vertex(render, t, 0);
            var b = Vertex(render, t, 1);
            var c = Vertex(render, t, 2);
            triangles[t * 3] = a;
            triangles[t * 3 + 1] = b - a;
            triangles[t * 3 + 2] = c - a;
            boxes[t] = Aabb.FromPoints([a, b, c]);
        }
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
            var entries = new double[64];
            for (int g = start; g < end; g++)
            {
                groupOcclusion[g] = Visibility(
                    bvh, triangles, positionArray[g], normalArray[g], liftArray[g],
                    directions, radius, lift, stack, entries);
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
        Bvh bvh, Vector3d[] triangles, in Vector3d position, in Vector3d normalSum,
        in Vector3d liftDirection, Vector3d[] directions, double radius, double lift,
        Bvh.NodeView[] stack, double[] entries)
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
            double nearest = NearestHit(bvh, triangles, ray, stack, entries);
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
    /// <para>
    /// The children are visited <b>nearer box first</b>, which is a pure speedup and not
    /// an approximation: the answer is a MINIMUM over hits, and a node is skipped only
    /// when the ray's entry parameter into its box already exceeds the best hit so far —
    /// nothing inside it can be closer. Ordering just reaches a tight bound sooner, so
    /// more subtrees fail that test. Each node's entry parameter is carried on the stack
    /// beside it rather than recomputed on pop, since the slab test that ordered the
    /// children has already produced it.
    /// </para>
    /// </summary>
    private static double NearestHit(
        Bvh bvh, Vector3d[] triangles, in Ray3d ray, Bvh.NodeView[] stack, double[] entries)
    {
        double nearest = 1.0;
        if (!ray.Intersects(bvh.Root.Bounds, out double rootEntry, out _))
            return nearest;

        int top = 0;
        stack[top] = bvh.Root;
        entries[top] = rootEntry;
        top++;
        while (top > 0)
        {
            top--;
            if (entries[top] >= nearest)
                continue;
            var node = stack[top];
            if (node.IsLeaf)
            {
                foreach (int triangle in node.Items)
                {
                    if (RayTriangle(ray, triangles[triangle * 3], triangles[triangle * 3 + 1],
                            triangles[triangle * 3 + 2], out double t) && t < nearest)
                        nearest = t;
                }
                continue;
            }

            var left = node.Left;
            var right = node.Right;
            bool hitLeft = ray.Intersects(left.Bounds, out double leftEntry, out _);
            bool hitRight = ray.Intersects(right.Bounds, out double rightEntry, out _);
            // The BVH is median-split and shallow, and this walk pushes at most one extra
            // slot per level, so 64 is the same depth budget Bvh's own traversals stackalloc.
            if (hitLeft && hitRight)
            {
                bool leftFirst = leftEntry <= rightEntry;
                stack[top] = leftFirst ? right : left;
                entries[top] = leftFirst ? rightEntry : leftEntry;
                top++;
                stack[top] = leftFirst ? left : right;
                entries[top] = leftFirst ? leftEntry : rightEntry;
                top++;
            }
            else if (hitLeft)
            {
                stack[top] = left;
                entries[top] = leftEntry;
                top++;
            }
            else if (hitRight)
            {
                stack[top] = right;
                entries[top] = rightEntry;
                top++;
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
    /// Moller-Trumbore test over a triangle stored as corner + two edge vectors; t is in
    /// units of the (unnormalized) ray direction, so a hit inside the search radius has
    /// t &lt; 1. Deliberately separate from the picking copy in
    /// <see cref="ViewportControl"/>: that one wants a world point for the measure tool.
    /// <para>
    /// Note that this is NOT an any-hit test, tempting as the name would be: the caller
    /// attenuates a hit by its distance (<c>1 − t</c>), so occlusion is a nearest-hit
    /// quantity and an early-out on the first intersection would change the shading. The
    /// early-out available here is the traversal's box pruning, which is exact.
    /// </para>
    /// </summary>
    private static bool RayTriangle(
        in Ray3d ray, in Vector3d a, in Vector3d e1, in Vector3d e2, out double t)
    {
        t = 0;
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
