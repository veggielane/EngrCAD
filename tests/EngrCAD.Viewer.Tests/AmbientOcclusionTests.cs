using EngrCAD.Implicit;
using EngrCAD.Mesh;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Viewer.Tests;

/// <summary>
/// The baked per-vertex ambient-occlusion signal (no GL — pure CPU geometry). The
/// ground truth is analytic in the qualitative sense every AO implementation must
/// satisfy: a convex body cannot occlude itself, a pocket floor sees less sky than the
/// open face beside it, and a bore wall sees less than either.
/// </summary>
public class AmbientOcclusionTests
{
    private static (RenderMesh Render, float[] Occlusion) Bake(Shape shape)
    {
        var render = RenderMesh.CreateFlat(new Part("p", shape).GetMesh());
        return (render, AmbientOcclusion.Bake(render));
    }

    /// <summary>Mean occlusion over the vertices satisfying a world-space predicate.</summary>
    private static double MeanWhere(RenderMesh mesh, float[] occlusion, Func<double, double, double, bool> where)
    {
        double sum = 0;
        int count = 0;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            if (!where(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2]))
                continue;
            sum += occlusion[v];
            count++;
        }
        Assert.True(count > 0, "predicate selected no vertices");
        return sum / count;
    }

    [Fact]
    public void ConvexBody_IsUnoccluded()
    {
        // A ray leaving a convex body along an outward hemisphere never re-enters it,
        // so every vertex of a box must come back fully open (exactly 1 — the estimate
        // has no noise floor to leak through).
        var (_, occlusion) = Bake(Shape.Box(4, 3, 2));
        Assert.All(occlusion, o => Assert.Equal(1f, o));

        var (_, sphere) = Bake(Shape.Sphere(2));
        Assert.All(sphere, o => Assert.True(o > 0.99f, $"convex sphere vertex occluded ({o})"));
    }

    [Fact]
    public void PocketFloor_IsDarkerThanTheOpenFaceBesideIt()
    {
        // 4x4x2 block with a 2x2 pocket 0.5 deep in the top face.
        var (mesh, occlusion) = Bake(Shape.Box(4, 4, 2) - Shape.Box(2, 2, 1).Translate(0, 0, 1));
        double floor = MeanWhere(mesh, occlusion, (x, y, z) => Math.Abs(z - 0.5) < 1e-6 && Math.Abs(x) < 1.5);
        double open = MeanWhere(mesh, occlusion, (x, y, z) => Math.Abs(z - 1) < 1e-6 && Math.Abs(x) > 1.5);

        Assert.True(floor < open - 0.1,
            $"pocket floor ({floor:F3}) should be clearly darker than the open top face ({open:F3})");
        Assert.True(open > 0.9, $"the open top face should stay near-unoccluded ({open:F3})");
        Assert.All(occlusion, o => Assert.InRange(o, 0f, 1f));
    }

    [Fact]
    public void BlindHoleFloor_IsStronglyOccluded()
    {
        // 6x6x3 block (z in [-1.5, 1.5]) with a 1.6-diameter hole 1.5 deep: the floor
        // sits at z = 0 and its vertices ring a concave corner, so it darkens hard.
        var (mesh, occlusion) = Bake(Shape.Box(6, 6, 3) - Shape.Cylinder(0.8, 2).Translate(0, 0, 1));
        double floor = MeanWhere(mesh, occlusion, (x, y, z) => Math.Abs(z) < 1e-6);
        double top = MeanWhere(mesh, occlusion, (x, y, z) => Math.Abs(z - 1.5) < 1e-6
                                                             && Math.Sqrt(x * x + y * y) > 2);
        Assert.True(floor < 0.75, $"blind-hole floor should be clearly occluded, got {floor:F3}");
        Assert.True(top > 0.9, $"the open top face should stay bright, got {top:F3}");
    }

    [Fact]
    public void ThroughBoreWall_ShowsTheVertexResolutionLimIt()
    {
        // KNOWN LIMITATION, locked here on purpose: baked occlusion lives on mesh
        // vertices, and a through-bore's wall is a two-row band whose only vertices are
        // the two rims — both of which sit at open faces. So a plain through hole gets
        // almost no occlusion, while pockets and blind holes (whose floors ring a
        // concave corner) do. If this assertion ever fails because the wall went dark,
        // the AO model changed (display-mesh refinement, or screen-space AO) and the
        // README's limitation note must change with it.
        var (mesh, occlusion) = Bake(Shape.Box(6, 6, 3) - Shape.Cylinder(0.8, 4));
        double wall = MeanWhere(mesh, occlusion,
            (x, y, z) => Math.Abs(Math.Sqrt(x * x + y * y) - 0.8) < 1e-2);
        Assert.True(wall > 0.75, $"unexpected: the through-bore wall darkened to {wall:F3}");
    }

    /// <summary>
    /// The bake is a NEAREST-hit query, not a boolean one, and this pins it so nobody
    /// "optimizes" it into an any-hit early-out. Occlusion is accumulated as
    /// <c>1 − t</c>, so a hit darkens in proportion to how close it is; a boolean test
    /// would count every hit alike and could stop traversing at the first one, which is
    /// far cheaper and a different renderer.
    /// <para>The construction removes every confound: at a radius fraction of 1 the
    /// search radius already equals the bounding diagonal, so no point of the mesh can be
    /// further away than that and GROWING the radius cannot bring in a single extra
    /// occluder. The occluder set is therefore identical in both bakes and only the
    /// distances scale — under a boolean rule the two results would be EQUAL. They are
    /// not: a bigger radius pushes every hit's normalized distance toward 0, so each
    /// contributes closer to a full unit of occlusion and the mesh darkens.</para>
    /// </summary>
    [Fact]
    public void Occlusion_AttenuatesWithDISTANCE_NotJustHitOrMiss()
    {
        var render = RenderMesh.CreateFlat(
            new Part("p", Shape.Box(4, 4, 2) - Shape.Box(2, 2, 1).Translate(0, 0, 1)).GetMesh());
        var tight = AmbientOcclusion.Bake(render, AmbientOcclusion.DefaultRays, radiusFraction: 1.0);
        // A radius a thousand diagonals wide IS the boolean bake: every hit's t collapses
        // to ~0, so each contributes a full unit of occlusion regardless of distance.
        var wide = AmbientOcclusion.Bake(render, AmbientOcclusion.DefaultRays, radiusFraction: 1000.0);

        // Averaged over the vertices that see an occluder at all — the ones the two rules
        // can possibly disagree about. (Over the whole mesh the gap is diluted to 0.008 by
        // the majority of vertices that are fully open under both.)
        var occluded = Enumerable.Range(0, tight.Length).Where(v => tight[v] < 0.999f).ToList();
        Assert.NotEmpty(occluded);
        double meanTight = occluded.Average(v => (double)tight[v]);
        double meanWide = occluded.Average(v => (double)wide[v]);
        Assert.True(meanTight > meanWide + 0.02,
            $"growing the search radius must darken a distance-attenuated bake " +
            $"({meanWide:F4} vs {meanTight:F4} over {occluded.Count} occluded vertices); " +
            "equal would mean the bake went boolean");
        // ... and never the other way round on any single vertex: a larger radius scales
        // every hit's t down, so every attenuation 1 - t grows.
        for (int v = 0; v < tight.Length; v++)
            Assert.True(wide[v] <= tight[v] + 1e-6, $"vertex {v} lightened at a larger radius");
    }

    [Fact]
    public void Bake_IsDeterministic()
    {
        // Byte-for-byte window/offscreen parity rests on this: the bake must not depend
        // on thread scheduling or on any random jitter.
        var render = RenderMesh.CreateFlat(
            new Part("p", Shape.Box(4, 4, 2) - Shape.Cylinder(1, 4)).GetMesh());
        Assert.Equal(AmbientOcclusion.Bake(render), AmbientOcclusion.Bake(render));
    }

    [Fact]
    public void For_CachesPerSourceMesh()
    {
        var mesh = new Part("p", Shape.Box(2, 2, 2) - Shape.Cylinder(0.4, 3)).GetMesh();
        var render = RenderMesh.CreateFlat(mesh);
        Assert.Same(AmbientOcclusion.For(mesh, render), AmbientOcclusion.For(mesh, render));
    }

    [Fact]
    public void RayCount_StaysWithinTheBudget()
    {
        Assert.Equal(AmbientOcclusion.DefaultRays, AmbientOcclusion.RayCount(AmbientOcclusion.DefaultRays, 1000));
        // 400k vertex groups x 32 rays would be 12.8M rays: halved down to the budget.
        int reduced = AmbientOcclusion.RayCount(AmbientOcclusion.DefaultRays, 400_000);
        Assert.True(reduced < AmbientOcclusion.DefaultRays, $"budget not applied ({reduced} rays)");
        Assert.True((long)reduced * 400_000 <= AmbientOcclusion.RayBudget || reduced == AmbientOcclusion.MinRays);
        // Never below the floor, however large the mesh.
        Assert.Equal(AmbientOcclusion.MinRays, AmbientOcclusion.RayCount(AmbientOcclusion.DefaultRays, 100_000_000));
    }

    // ---- streaming: the window never waits for a bake ----

    [Fact]
    public void TryGet_NeverBakes_AndSeesWhatForProduced()
    {
        // The contract the render thread relies on: TryGet is a pure cache read, so an
        // unbaked part is reported as "no occlusion yet" (draws flat-lit) rather than
        // stalling the frame by however long the bake takes.
        var mesh = new Part("p", Shape.Box(3, 3, 1) - Shape.Cylinder(0.5, 2)).GetMesh();
        var render = RenderMesh.CreateFlat(mesh);
        Assert.Null(AmbientOcclusion.TryGet(mesh));

        var baked = AmbientOcclusion.For(mesh, render);
        Assert.Same(baked, AmbientOcclusion.TryGet(mesh));
    }

    [Fact]
    public async Task BakeInBackground_PublishesEveryPart_CheapestFirst()
    {
        // Three parts of clearly different triangle counts; the queue is ordered by cost
        // so most of a scene gains its occlusion in the first moments.
        var small = new Part("small", Shape.Box(2, 2, 2) - Shape.Box(1, 1, 1).Translate(0, 0, 1));
        var medium = new Part("medium", Shape.Cylinder(1, 2) - Shape.Cylinder(0.4, 3));
        var large = new Part("large", Shape.Sphere(1) - Shape.Cylinder(0.3, 3));
        List<Part> parts = [large, small, medium];
        foreach (var part in parts)
            part.GetMesh();

        var completed = new TaskCompletionSource<(int Count, TimeSpan Elapsed)>();
        int callbacks = 0;
        AmbientOcclusion.BakeInBackground(
            parts, onPartBaked: _ => Interlocked.Increment(ref callbacks),
            onFinished: (count, elapsed) => completed.SetResult((count, elapsed)),
            CancellationToken.None);

        var (bakedCount, _) = await completed.Task.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(parts.Count, bakedCount);
        Assert.Equal(parts.Count, callbacks);
        foreach (var part in parts)
            Assert.NotNull(AmbientOcclusion.TryGet(part.GetMesh()));

        // Streamed results are the SAME data the blocking path produces — window and
        // headless shading cannot diverge, they just arrive at different times.
        var mesh = medium.GetMesh();
        Assert.Equal(AmbientOcclusion.Bake(RenderMesh.CreateFlat(mesh)), AmbientOcclusion.TryGet(mesh));
    }

    [Fact]
    public async Task BakeInBackground_AlreadyCachedParts_ReportNothing()
    {
        // A tab revisit or an AO toggle re-queues parts that are already baked; that must
        // be free and must not post a status line claiming work was done.
        var part = new Part("cached", Shape.Box(2, 2, 2) - Shape.Cylinder(0.3, 3));
        var mesh = part.GetMesh();
        AmbientOcclusion.For(mesh, RenderMesh.CreateFlat(mesh));

        int reported = 0;
        int callbacks = 0;
        AmbientOcclusion.BakeInBackground(
            [part], onPartBaked: _ => Interlocked.Increment(ref callbacks),
            onFinished: (_, _) => Interlocked.Increment(ref reported), CancellationToken.None);

        // No completion callback fires, so just give the job time to run through.
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref callbacks));
        Assert.Equal(0, Volatile.Read(ref reported));
    }

    [Fact]
    public async Task BakeInBackground_Cancellation_DropsTheRestOfTheQueue()
    {
        // A scene swap cancels the queue; whatever was already published stays cached
        // (it is keyed by mesh, and a mesh's occlusion never goes stale).
        var parts = new List<Part>();
        for (int i = 0; i < 6; i++)
            parts.Add(new Part($"p{i}", Shape.Box(2 + i * 0.1, 2, 2) - Shape.Cylinder(0.4, 3)));
        foreach (var part in parts)
            part.GetMesh();

        using var cts = new CancellationTokenSource();
        var first = new TaskCompletionSource();
        int reported = 0;
        AmbientOcclusion.BakeInBackground(
            parts,
            onPartBaked: _ =>
            {
                // Runs ON the bake thread between parts, so cancelling here is seen at
                // the very next loop iteration: exactly one part gets baked.
                cts.Cancel();
                first.TrySetResult();
            },
            onFinished: (_, _) => Interlocked.Increment(ref reported),
            cts.Token);

        await first.Task.WaitAsync(TimeSpan.FromMinutes(1));
        await Task.Delay(300);
        int cached = parts.Count(p => AmbientOcclusion.TryGet(p.GetMesh()) is not null);
        Assert.Equal(1, cached);
        Assert.Equal(0, Volatile.Read(ref reported));
    }

    [Fact]
    public void EmptyMesh_BakesNothing()
    {
        var empty = new RenderMesh { Positions = [], Normals = [], Indices = [] };
        Assert.Empty(AmbientOcclusion.Bake(empty));
    }

    [Fact]
    public void DenseMeshes_SkipTheBakeEntirely()
    {
        // Above MaxTriangles the bake is skipped (see the constant's rationale: the
        // per-ray cost explodes in lattice-like geometry). Everything comes back fully
        // open, which is exactly what "no occlusion" means to the shader.
        var lattice = RenderMesh.CreateFlat(
            new Part("l", Shape.Sphere(16).Lattice(Sdf.Gyroid(cellSize: 12, thickness: 1.2)))
                .GetMesh(new MeshQuality { SdfResolution = 110 }));
        Assert.True(lattice.TriangleCount > AmbientOcclusion.MaxTriangles,
            $"the fixture stopped being dense ({lattice.TriangleCount} triangles)");
        Assert.All(AmbientOcclusion.Bake(lattice), o => Assert.Equal(1f, o));
    }
}
