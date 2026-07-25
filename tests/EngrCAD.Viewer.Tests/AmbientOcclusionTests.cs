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
