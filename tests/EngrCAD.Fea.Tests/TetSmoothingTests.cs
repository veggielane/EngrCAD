using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Optimization-based smoothing — the post-pass for the one defect Delaunay refinement
/// provably cannot bound. Everything here is either a measurement of what it buys or a
/// statement of what it must not disturb.
/// </summary>
public class TetSmoothingTests(ITestOutputHelper output)
{
    private static TetMesh RefinedBox(double size = 20, double target = 3.0) =>
        TetMesher.Mesh(
            MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(size, size, size))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = target });

    [Fact]
    public void ItRaisesTheWorstDihedralAndRemovesSlivers()
    {
        var mesh = RefinedBox();
        var smoothed = TetSmoothing.Smooth(mesh, null, out var report);

        output.WriteLine(report.ToString());

        Assert.True(report.MinDihedralAfter > report.MinDihedralBefore,
            $"worst dihedral {report.MinDihedralBefore:F3} -> {report.MinDihedralAfter:F3}");
        Assert.True(report.SliversAfter < report.SliversBefore,
            $"slivers {report.SliversBefore} -> {report.SliversAfter}");
        Assert.True(report.VerticesMoved > 0);
        Assert.Equal(mesh.TetCount, smoothed.TetCount);
        Assert.Equal(mesh.VertexCount, smoothed.VertexCount);
    }

    /// <summary>
    /// The volume identity is EXACT in principle — the boundary never moves and the elements go
    /// on tiling the same region — so any drift is pure round-off. That is the assertion with
    /// teeth: a smoother that let an element invert, or that moved a boundary vertex, would
    /// show up here immediately.
    /// </summary>
    [Fact]
    public void ItPreservesTheBoundaryAndTheVolumeExactly()
    {
        var mesh = RefinedBox();
        var boundaryBefore = mesh.BoundaryFacets.ToArray();
        var smoothed = TetSmoothing.Smooth(mesh, null, out var report);

        Assert.True(report.VolumeChangeRelative < 1e-12,
            $"volume drift {report.VolumeChangeRelative:E3}");

        // Every boundary vertex is bit-for-bit where it was.
        foreach (var f in boundaryBefore)
            foreach (int v in (int[])[f.V0, f.V1, f.V2])
                Assert.Equal(mesh.Position(v), smoothed.Position(v));

        // ...and the boundary itself is the same facets in the same order.
        Assert.Equal(boundaryBefore.Length, smoothed.BoundaryFacetCount);
        for (int i = 0; i < boundaryBefore.Length; i++)
            Assert.Equal(boundaryBefore[i], smoothed.BoundaryFacets[i]);

        // The 20^3 box's volume, still.
        Assert.Equal(8000.0, smoothed.Volume, 6);
    }

    /// <summary>
    /// Topology is untouched, which is what lets every downstream guarantee ride through:
    /// region ids, element order and connectivity all survive verbatim.
    /// </summary>
    [Fact]
    public void ItChangesNoTopology()
    {
        var mesh = RefinedBox();
        var smoothed = TetSmoothing.Smooth(mesh);

        for (int t = 0; t < mesh.TetCount; t++)
        {
            Assert.Equal(mesh.GetTet(t), smoothed.GetTet(t));
            Assert.Equal(mesh.RegionOf(t), smoothed.RegionOf(t));
        }
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var mesh = RefinedBox();
        var first = TetSmoothing.Smooth(mesh);
        var second = TetSmoothing.Smooth(mesh);

        for (int v = 0; v < first.VertexCount; v++)
        {
            var a = first.Position(v);
            var b = second.Position(v);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.X), BitConverter.DoubleToInt64Bits(b.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Y), BitConverter.DoubleToInt64Bits(b.Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.Z), BitConverter.DoubleToInt64Bits(b.Z));
        }
    }

    /// <summary>
    /// A boundary layer is a DELIBERATE anisotropy, and a smoother that "repairs" it into
    /// isotropy would destroy the resolution it exists to provide. Every vertex touching a
    /// stretched element is frozen, so a layered mesh's stack comes back untouched.
    /// </summary>
    [Fact]
    public void ItRefusesToRepairADeliberateBoundaryLayer()
    {
        var surface = MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(20, 20, 20)));
        var mesh = TetMesher.Mesh(surface, new TetMeshOptions
        {
            BoundaryLayer = new BoundaryLayerSpec
            {
                Wall = Facets.All,
                FirstLayerThickness = 0.5,
                LayerCount = 3,
                GrowthRatio = 1.2,
            },
        }, out var meshReport);

        int layerElements = meshReport.BoundaryLayer!.Value.ElementCount;
        Assert.True(layerElements > 0);

        var before = TetQuality.Analyze(mesh);
        Assert.True(before.AnisotropicCount > 0, "the fixture must actually be anisotropic");

        var smoothed = TetSmoothing.Smooth(mesh, null, out var report);
        var after = TetQuality.Analyze(smoothed);

        output.WriteLine(report.ToString());

        // The layer is reported as frozen...
        Assert.True(report.FrozenAnisotropicVertices > 0);

        // ...and every stack element is bit-for-bit where it was.
        for (int t = 0; t < layerElements; t++)
        {
            var tet = mesh.GetTet(t);
            for (int i = 0; i < 4; i++)
                Assert.Equal(mesh.Position(tet[i]), smoothed.Position(tet[i]));
        }

        // The anisotropy itself survives — the stack is still a stack.
        Assert.Equal(before.AnisotropicCount, after.AnisotropicCount);
        Assert.Equal(before.MaxStretch, after.MaxStretch, 9);
    }

    /// <summary>
    /// The residual sliver count depends on the INPUT, and pinning that is what stops the
    /// measured "190 -> 0" being read as a guarantee.
    ///
    /// <para>A pattern search is a heuristic local optimizer, so a small difference in the
    /// input changes which candidate wins a near-tie and with it the whole path. Two 20-cubes
    /// that differ only in how their faces are triangulated — `MeshPrimitives.Box` against the
    /// B-Rep tessellation of `Shape.Box` — both start from 190 slivers and finish at <b>0 and
    /// 2</b>. Note what is NOT the cause, since it was the first guess and it is wrong: it is
    /// not translation (the same primitive anchored at a corner and centred on the origin both
    /// reach 0) and not the build (Release and Debug agree bit for bit).</para>
    ///
    /// <para>So the assertion here — and everywhere else in this file — is a strict DECREASE,
    /// never zero.</para>
    /// </summary>
    [Fact]
    public void TheResidualDependsOnTheInputTriangulation()
    {
        var primitive = TetMesher.Mesh(
            MeshPrimitives.Box(new Aabb(new Vector3d(-10, -10, -10), new Vector3d(10, 10, 10))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 3.0 });
        var tessellated = TetMesher.Mesh(
            EngrCAD.Modeling.Shape.Box(20, 20, 20).ToMesh(),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 3.0 });

        TetSmoothing.Smooth(primitive, null, out var fromPrimitive);
        TetSmoothing.Smooth(tessellated, null, out var fromTessellation);
        output.WriteLine($"MeshPrimitives.Box : {fromPrimitive}");
        output.WriteLine($"Shape.Box().ToMesh : {fromTessellation}");

        // Both improve substantially — that is what the pass promises...
        foreach (var report in new[] { fromPrimitive, fromTessellation })
        {
            Assert.True(report.SliversAfter < report.SliversBefore,
                $"slivers {report.SliversBefore} -> {report.SliversAfter}");
            Assert.True(report.MinDihedralAfter > report.MinDihedralBefore);
            // ...and the volume is untouched however the search happened to go, which is the
            // invariant that IS exact rather than heuristic.
            Assert.True(report.VolumeChangeRelative < 1e-12);
        }
    }

    /// <summary>Translation is NOT what moves the answer — measured, both anchorings agree.</summary>
    [Fact]
    public void TranslatingTheBodyDoesNotChangeTheOutcome()
    {
        var atCorner = TetMesher.Mesh(
            MeshPrimitives.Box(new Aabb(Vector3d.Zero, new Vector3d(20, 20, 20))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 3.0 });
        var atOrigin = TetMesher.Mesh(
            MeshPrimitives.Box(new Aabb(new Vector3d(-10, -10, -10), new Vector3d(10, 10, 10))),
            new TetMeshOptions { RefineQuality = true, MaxElementSize = 3.0 });

        TetSmoothing.Smooth(atCorner, null, out var corner);
        TetSmoothing.Smooth(atOrigin, null, out var origin);

        Assert.Equal(corner.SliversBefore, origin.SliversBefore);
        Assert.Equal(corner.SliversAfter, origin.SliversAfter);
    }

    /// <summary>
    /// Zero passes is the identity, so the option is a real dial rather than an on/off with a
    /// surprising floor.
    /// </summary>
    [Fact]
    public void ZeroPassesIsTheIdentity()
    {
        var mesh = RefinedBox();
        var smoothed = TetSmoothing.Smooth(mesh, new TetSmoothOptions { Passes = 0 }, out var report);

        Assert.Equal(0, report.VerticesMoved);
        for (int v = 0; v < mesh.VertexCount; v++)
            Assert.Equal(mesh.Position(v), smoothed.Position(v));
    }

    /// <summary>
    /// Smoothing must not break the physics. The structural patch test reproduces a constant
    /// strain state exactly only if the elements genuinely tile the body, so running it through
    /// a smoothed mesh is a direct check that nothing inverted or tore.
    /// </summary>
    [Fact]
    public void ASmoothedMeshStillReproducesAConstantStrainState()
    {
        var mesh = TetSmoothing.Smooth(RefinedBox(20, 4.0));
        var analysis = AnalysisMesh.Of(mesh);
        var model = new StructuralModel(analysis, Materials.Steel);

        // u = (1e-3 x, 0, 0): a uniform axial stretch, prescribed on every boundary node.
        static Vector3d Exact(Vector3d p) => new(1e-3 * p.X, 0, 0);

        var boundaryNodes = new HashSet<int>();
        foreach (var f in mesh.BoundaryFacets)
        {
            boundaryNodes.Add(f.V0);
            boundaryNodes.Add(f.V1);
            boundaryNodes.Add(f.V2);
        }
        foreach (int n in boundaryNodes.Order())
            model.PrescribeNode(n, Exact(mesh.Position(n)), Dof.All);

        var results = StructuralSolver.Solve(model);

        double worst = 0;
        for (int n = 0; n < mesh.VertexCount; n++)
            worst = Math.Max(worst, (results.Displacement[n] - Exact(mesh.Position(n))).Length);

        // Relative to the largest prescribed displacement (1e-3 * 20).
        double relative = worst / 2e-2;
        output.WriteLine($"patch-test residual {relative:E3} relative");
        Assert.True(relative < 1e-10, $"patch test residual {relative:E3}");
    }
}
