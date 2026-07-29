using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Every way a structural model can be wrong, and the message it gets. A solver that
/// returns plausible numbers for an ill-posed model is worse than one that will not run,
/// because nothing downstream can tell the difference.
/// </summary>
public class FeaRefusalTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("refusal steel", 210_000, 0.3, 7.85e-9);
    private static readonly Vector3d Size = new(20, 15, 10);

    private static AnalysisMesh Block() =>
        AnalysisMesh.Of(StructuredTetMesh.Box(Vector3d.Zero, Size, 3, 2, 2));

    [Fact]
    public void AnUnrestrainedBody_IsRefusedNamingAllSixModes()
    {
        var model = new StructuralModel(Block(), Steel);
        model.Force(Facets.Tag(StructuredTetMesh.ZMax), new Vector3d(0, 0, -100));

        var ex = Assert.Throws<FeaException>(() => StructuralSolver.Solve(model));
        output.WriteLine(ex.Message);
        Assert.Contains("6 rigid-body modes survive", ex.Message);
        Assert.Contains("3-2-1", ex.Message);
    }

    [Fact]
    public void ASinglePinnedNode_LeavesThreeRotationsAndTheMessageLocatesTheirAxes()
    {
        // One fully fixed node removes the three translations and nothing else, leaving
        // every rotation about every axis through it. The interesting part is the message:
        // "rotation about Y" is useless without saying where the axis is.
        //
        // The node pinned here is the block's CENTROID, deliberately. An axis is a line
        // and the reported point is its closest approach to the body's centroid, so
        // pinning the centroid is the one case where the quoted point is the pinned node
        // itself — pin a corner instead and the same three lines come back quoted at a
        // different point on each, which is correct and unreadable.
        var mesh = AnalysisMesh.Of(StructuredTetMesh.Box(Vector3d.Zero, Size, 4, 2, 2));
        var model = new StructuralModel(mesh, Steel);
        var centroid = Size * 0.5;
        model.FixNode(FeaEquilibriumTests.Node(mesh, centroid));
        model.Force(Facets.Tag(StructuredTetMesh.ZMax), new Vector3d(0, 0, -100));

        var ex = Assert.Throws<FeaException>(() => StructuralSolver.Solve(model));
        output.WriteLine(ex.Message);
        Assert.Contains("3 rigid-body modes survive", ex.Message);
        Assert.Contains("rotation about the axis through", ex.Message);
        Assert.DoesNotContain("translation along", ex.Message);
        Assert.Contains("(10, 7.5, 5)", ex.Message);
    }

    [Fact]
    public void ASlidingSupport_LeavesTheMotionItPermits()
    {
        // A whole face fixed in Z only: the body still slides in X and Y and spins about
        // Z. Three modes, and two of them are pure translations.
        var model = new StructuralModel(Block(), Steel);
        model.Fix(StructuredTetMesh.ZMin, Dof.Z);
        model.Force(Facets.Tag(StructuredTetMesh.ZMax), new Vector3d(0, 0, -100));

        var ex = Assert.Throws<FeaException>(() => StructuralSolver.Solve(model));
        output.WriteLine(ex.Message);
        Assert.Contains("3 rigid-body modes survive", ex.Message);
        Assert.Contains("translation along", ex.Message);
    }

    [Fact]
    public void AFloatingSECONDBody_IsRefusedByBodyEvenThoughTheFirstIsFullyFixed()
    {
        // The case a whole-model rigid-mode check cannot see: body A is clamped, body B
        // floats, and no motion of B alone is in the span of the WHOLE model's six rigid
        // modes. Checking per connected component is the structural fix.
        var a = StructuredTetMesh.Box(Vector3d.Zero, Size, 2, 2, 2);
        var b = StructuredTetMesh.Box(new Vector3d(100, 0, 0), Size, 2, 2, 2);
        var mesh = AnalysisMesh.Of(Combine(a, b));
        var model = new StructuralModel(mesh, Steel);

        // Clamp everything in the first body's half of space.
        foreach (int node in model.NodesOn(f => f.Centroid.X < 50 && Math.Abs(f.Normal.X + 1) < 1e-9))
            model.FixNode(node);
        model.Gravity(Materials.GravityMillimetres);

        var ex = Assert.Throws<FeaException>(() => StructuralSolver.Solve(model));
        output.WriteLine(ex.Message);
        Assert.Contains("Body 2 of 2", ex.Message);
        Assert.Contains("6 rigid-body modes survive", ex.Message);
    }

    [Fact]
    public void ASelectorThatMatchesNothing_IsRefusedAtTheCallNamingTheTagsThatExist()
    {
        var model = new StructuralModel(Block(), Steel);

        var ex = Assert.Throws<FeaException>(() => model.Fix(Facets.Tag(99)));
        output.WriteLine(ex.Message);
        Assert.Contains("selected no boundary facets", ex.Message);
        Assert.Contains("tags: 0, 1, 2, 3, 4, 5", ex.Message);
        Assert.Contains("TetMeshOptions.FacetTags", ex.Message);

        var geometric = Assert.Throws<FeaException>(
            () => model.Pressure(Facets.OnPlane(new Vector3d(0, 0, 1000), Vector3d.UnitZ), 5));
        Assert.Contains("Pressure selected no boundary facets", geometric.Message);
    }

    [Fact]
    public void EveryDegreeOfFreedomRestrained_SaysThereIsNothingToSolve()
    {
        var mesh = Block();
        var model = new StructuralModel(mesh, Steel);
        for (int node = 0; node < mesh.NodeCount; node++)
            model.FixNode(node);

        var ex = Assert.Throws<FeaException>(() => StructuralSolver.Solve(model));
        output.WriteLine(ex.Message);
        Assert.Contains("nothing to solve for", ex.Message);
    }

    [Fact]
    public void AnElementWhoseDoublePrecisionVolumeVanishes_IsRefusedByName()
    {
        // These four points are VERBATIM from a tetrahedron the Delaunay mesher produced
        // for a 100 x 10 x 10 beam at a 5.0 size target. Its exact signed volume is
        // strictly positive — Predicates3d says +1, so TetMesh accepts it — while the
        // double-precision triple product is exactly 0.0. Assembled, it contributes a
        // stiffness of the wrong sign, so the solver refuses it rather than absorbing it.
        Vector3d[] corners =
        [
            new(96.875, 9.375, 0),
            new(93.86837301508133, 8.90625, 0.6370888425630207),
            new(96.875, 10, 0.625),
            new(93.86837301508133, 9.36291115743698, 1.09375),
        ];
        Assert.Equal(1, Predicates3d.SignedVolume6Sign(corners[0], corners[1], corners[2], corners[3]));
        Assert.Equal(0.0, TetMesh.SignedVolume(corners[0], corners[1], corners[2], corners[3]));

        var mesh = AnalysisMesh.Of(StructuredTetMesh.SingleTet(corners));
        var model = new StructuralModel(mesh, Steel);
        var ex = Assert.Throws<FeaException>(() => StructuralSolver.Solve(model));
        output.WriteLine(ex.Message);
        Assert.Contains("1 of 1 elements have a non-positive Jacobian", ex.Message);
        Assert.Contains("wrong sign", ex.Message);
        Assert.Contains("sliver-removal gap", ex.Message);
        Assert.DoesNotContain("and more", ex.Message);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.6)]
    [InlineData(-1.0)]
    public void AnImpossiblePoissonRatio_IsRefusedWithTheReason(double nu)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Material("bad", 200_000, nu));
        Assert.Contains("(-1, 0.5)", ex.Message);
        if (nu >= 0.5)
            Assert.Contains("incompressible", ex.Message);
    }

    [Fact]
    public void ANegativeOrInfiniteModulus_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Material("bad", -1, 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Material("bad", double.PositiveInfinity, 0.3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Material("bad", 200_000, 0.3, -1));
        Assert.Throws<ArgumentException>(() => new Material(" ", 200_000, 0.3));
    }

    /// <summary>
    /// A modulus of ZERO is legal to BUILD and refused where it is USED — the placement that
    /// changed when <see cref="Material"/> moved down to EngrCAD.Core to serve the document
    /// model as well. A material with a name and a density is what a bill of materials is
    /// made of, so the constructor cannot demand elasticity; the structural model can, and
    /// says which property is missing.
    /// </summary>
    [Fact]
    public void AMaterialWithNoModulus_BuildsButIsRefusedByTheStructuralModel()
    {
        var document = new Material("mystery alloy", density: 7.8e-9);
        Assert.False(document.HasElasticity);

        var ex = Assert.Throws<FeaException>(() => new StructuralModel(Block(), document));
        output.WriteLine(ex.Message);
        Assert.Contains("mystery alloy", ex.Message);
        Assert.Contains("no Young's modulus", ex.Message);
        // The reason matters as much as the refusal: without it the solve does not go wrong,
        // it assembles an identically zero stiffness and reports rigid-body modes instead.
        Assert.Contains("identically zero", ex.Message);
        Assert.Contains("WithElasticity", ex.Message);

        // Same refusal on the per-region assignment, so a multi-body model cannot slip one in.
        var model = new StructuralModel(Block(), Steel);
        Assert.Throws<FeaException>(() => model.SetMaterial(1, document));

        // And the material a solve WILL take is the same object with a modulus.
        _ = new StructuralModel(Block(), document.WithElasticity(200_000, 0.3));
    }

    /// <summary>Two tet meshes side by side as one mesh — disjoint bodies.</summary>
    private static TetMesh Combine(TetMesh a, TetMesh b)
    {
        var positions = new Vector3d[a.VertexCount + b.VertexCount];
        for (int v = 0; v < a.VertexCount; v++)
            positions[v] = a.Position(v);
        for (int v = 0; v < b.VertexCount; v++)
            positions[a.VertexCount + v] = b.Position(v);

        var tets = new int[(a.TetCount + b.TetCount) * 4];
        var regions = new int[a.TetCount + b.TetCount];
        for (int t = 0; t < a.TetCount; t++)
        {
            var e = a.GetTet(t);
            for (int i = 0; i < 4; i++)
                tets[4 * t + i] = e[i];
        }
        for (int t = 0; t < b.TetCount; t++)
        {
            var e = b.GetTet(t);
            for (int i = 0; i < 4; i++)
                tets[4 * (a.TetCount + t) + i] = a.VertexCount + e[i];
            regions[a.TetCount + t] = 1;
        }

        var facets = new List<TetFacet>();
        foreach (var f in a.BoundaryFacets)
            facets.Add(f);
        foreach (var f in b.BoundaryFacets)
        {
            facets.Add(new TetFacet(
                a.VertexCount + f.V0, a.VertexCount + f.V1, a.VertexCount + f.V2,
                a.TetCount + f.Tet, f.SourceTriangle));
        }
        return new TetMesh(positions, tets, regions, [.. facets]);
    }
}
