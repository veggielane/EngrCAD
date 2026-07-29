using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The API's own contracts — the traps a physics test never reaches: condition ordering,
/// option validation, guard clauses, scale-freedom of the geometric selectors, load-case
/// reuse, and the sampling path that only fires when the display mesh is NOT the analysis
/// input.
/// </summary>
public class FeaApiContractTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("contract steel", 210_000, 0.3, 7.85e-9);
    private static readonly Vector3d Size = new(30, 20, 10);

    private static AnalysisMesh Block(ElementOrder order = ElementOrder.Linear, int n = 2)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 3 * n, 2 * n, n);
        return order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
    }

    // ---- Fix vs Prescribe ordering -------------------------------------------------

    [Fact]
    public void FixClearsAPrescribedDisplacement_AndTheTwoOrderingsCommute()
    {
        // A fix is a prescribe-to-zero. Without that, Prescribe(...).Fix(...) would keep
        // the enforced deflection while both the API and the condition log said "fix" —
        // and the two orderings would give different answers, which is the tell.
        var mesh = Block();
        var pull = new Vector3d(0, 0, 0.05);

        var prescribeThenFix = new StructuralModel(mesh, Steel);
        prescribeThenFix.Prescribe(Facets.Tag(StructuredTetMesh.XMin), pull);
        prescribeThenFix.Fix(StructuredTetMesh.XMin);
        prescribeThenFix.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -500));

        var fixOnly = new StructuralModel(mesh, Steel);
        fixOnly.Fix(StructuredTetMesh.XMin);
        fixOnly.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -500));

        foreach (int node in fixOnly.NodesOn(Facets.Tag(StructuredTetMesh.XMin)))
        {
            Assert.Equal(Vector3d.Zero, prescribeThenFix.PrescribedOf(node));
            Assert.Equal(Dof.All, prescribeThenFix.RestraintOf(node));
        }

        var a = StructuralSolver.Solve(prescribeThenFix);
        var b = StructuralSolver.Solve(fixOnly);
        output.WriteLine(
            $"prescribe-then-fix energy {a.StrainEnergy:G12}, fix-only {b.StrainEnergy:G12}");
        Assert.Equal(b.StrainEnergy, a.StrainEnergy, Math.Abs(b.StrainEnergy) * 1e-12);

        // And the reverse order is the same model: a later Prescribe still wins, because
        // it is the more specific statement.
        var fixThenPrescribe = new StructuralModel(mesh, Steel);
        fixThenPrescribe.Fix(StructuredTetMesh.XMin);
        fixThenPrescribe.Prescribe(Facets.Tag(StructuredTetMesh.XMin), pull);
        int sample = fixThenPrescribe.NodesOn(Facets.Tag(StructuredTetMesh.XMin))[0];
        Assert.Equal(pull, fixThenPrescribe.PrescribedOf(sample));
    }

    // ---- option validation ---------------------------------------------------------

    [Fact]
    public void EstimateConditionWithAnIterativeSolve_IsRefusedRatherThanSilentlyNull()
    {
        var model = new StructuralModel(Block(), Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -500));

        var ex = Assert.Throws<FeaException>(() => StructuralSolver.Solve(model,
            new StructuralSolveOptions
            {
                Method = FeaSolveMethod.ConjugateGradient,
                EstimateCondition = true,
            }));
        output.WriteLine(ex.Message);
        Assert.Contains("EstimateCondition", ex.Message);
        Assert.Contains("Direct", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveConditionIterationCount_IsRefusedAtTheOption(int iterations)
    {
        // At zero, both ends of the spectrum would report the Rayleigh quotient of a START
        // VECTOR and the ratio would come out near 1 — a plausible-looking wrong answer.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StructuralSolveOptions { ConditionIterations = iterations });
    }

    // ---- guard clauses -------------------------------------------------------------

    [Fact]
    public void OutOfRangeIndicesAreRefused_IncludingTheQuadraticLocalNodeOnALinearMesh()
    {
        var mesh = Block();
        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -500));
        var results = StructuralSolver.Solve(model);

        Assert.Throws<ArgumentOutOfRangeException>(() => model.RestraintOf(mesh.NodeCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.PrescribedOf(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.ForceOf(mesh.NodeCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => results.DisplacementAt(mesh.NodeCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => results.ReactionAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => results.ElementStress(mesh.ElementCount));

        // The one that would otherwise read ANOTHER node's natural coordinates from a
        // separate static table and return a plausible wrong stress: local node 4..9 is
        // valid on a quadratic mesh and out of range on a linear one.
        Assert.Equal(4, mesh.NodesPerElement);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => results.ElementStressAtNode(0, 4));
        output.WriteLine(ex.Message);
        Assert.Contains("4 nodes", ex.Message);
        _ = results.ElementStressAtNode(0, 3);   // in range, must not throw
    }

    [Fact]
    public void AZeroPlaneNormal_IsRefusedByNameRatherThanByVectorMath()
    {
        var ex = Assert.Throws<FeaException>(
            () => Facets.OnPlane(Vector3d.Zero, Vector3d.Zero));
        Assert.Contains("non-zero plane normal", ex.Message);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Facets.OnPlane(Vector3d.Zero, Vector3d.UnitZ, relativeTolerance: 0));
    }

    // ---- scale freedom -------------------------------------------------------------

    [Theory]
    [InlineData(0.001)]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    public void TheGeometricSelectorsAreScaleFree(double scale)
    {
        // Facets.OnPlane measures against each facet's OWN size, so the same model at
        // three scales must select the same facets. An absolute length there would select
        // everything at 0.001x and nothing at 1000x — the mate solver's 0.01x/1x/1000x
        // convention, applied to a selector.
        var size = Size * scale;
        var mesh = AnalysisMesh.Of(StructuredTetMesh.Box(Vector3d.Zero, size, 6, 4, 2));
        var model = new StructuralModel(mesh, Steel);

        int byPlane = model.FacetsMatching(Facets.OnPlane(Vector3d.Zero, Vector3d.UnitX)).Count;
        int byTag = model.FacetsMatching(Facets.Tag(StructuredTetMesh.XMin)).Count;
        int facing = model.FacetsMatching(Facets.FacingAlong(-Vector3d.UnitX, 10)).Count;

        output.WriteLine(
            $"scale {scale,8}: OnPlane {byPlane} facets, Tag {byTag}, FacingAlong {facing}");
        Assert.Equal(byTag, byPlane);
        Assert.Equal(byTag, facing);
        Assert.True(byPlane > 0);
    }

    // ---- load-case reuse -----------------------------------------------------------

    [Fact]
    public void ClearLoadsKeepsSupportsAndMaterials_AndTheSecondCaseMatchesAFreshModel()
    {
        var mesh = Block();
        var reused = new StructuralModel(mesh, Steel);
        reused.Fix(StructuredTetMesh.XMin);
        reused.Gravity(Materials.GravityMillimetres);
        var first = StructuralSolver.Solve(reused);

        reused.ClearLoads();
        Assert.Equal(Vector3d.Zero, reused.AppliedForce);
        reused.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -700));
        var second = StructuralSolver.Solve(reused);

        var fresh = new StructuralModel(mesh, Steel);
        fresh.Fix(StructuredTetMesh.XMin);
        fresh.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -700));
        var reference = StructuralSolver.Solve(fresh);

        output.WriteLine(
            $"gravity case energy {first.StrainEnergy:G8}; reused second case "
            + $"{second.StrainEnergy:G12} vs fresh {reference.StrainEnergy:G12}");
        Assert.Equal(reference.StrainEnergy, second.StrainEnergy,
            Math.Abs(reference.StrainEnergy) * 1e-12);
        Assert.NotEqual(first.StrainEnergy, second.StrainEnergy);
    }

    // ---- multi-material gravity ----------------------------------------------------

    [Fact]
    public void GravityWeighsEachRegionWithItsOwnDensity()
    {
        // The MaterialOf fallback and the per-region density in one check: two halves of
        // one bar, and the exact weight is the sum of the two densities' contributions.
        var dense = new Material("dense", 210_000, 0.3, 1.0e-8);
        var light = new Material("light", 70_000, 0.33, 2.5e-9);
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 6, 4, 2, p => p.Z < Size.Z * 0.5 ? 0 : 1);
        var mesh = AnalysisMesh.Of(tets);

        var model = new StructuralModel(mesh, dense).SetMaterial(1, light);
        model.Fix(StructuredTetMesh.ZMin);
        model.Gravity(Materials.GravityMillimetres);
        var results = StructuralSolver.Solve(model);

        double half = Size.X * Size.Y * Size.Z * 0.5;
        double expected = -(dense.Density + light.Density) * half * Materials.GravityMillimetres.Length;

        output.WriteLine(
            $"two-density bar: applied {results.Report.AppliedForce.Z:G10} N, exact {expected:G10} N; "
            + $"region 0 is '{model.MaterialOf(0).Name}', region 1 is "
            + $"'{model.MaterialOf(mesh.ElementCount - 1).Name}'");

        Assert.Equal(expected, results.Report.AppliedForce.Z, Math.Abs(expected) * 1e-12);
        Assert.Equal(-expected, results.Report.ReactionForce.Z, Math.Abs(expected) * 1e-9);
        // The undeclared region falls back to the constructor's material, not to nothing.
        Assert.Equal(dense, model.MaterialOf(0));
        Assert.Equal(light, model.MaterialOf(mesh.ElementCount - 1));
    }

    // ---- nodal averaging -----------------------------------------------------------

    [Fact]
    public void TheAveragingRuleChangesTheAnswerOnlyWhereElementVolumesDiffer()
    {
        // On a uniform mesh, Kuhn's six tetrahedra all have the same volume, so weighting
        // by it cannot change anything — a property worth pinning, because it means a
        // difference on a GRADED mesh is the rule doing its job rather than noise.
        var uniform = new StructuralModel(Block(ElementOrder.Linear, 3), Steel);
        uniform.Fix(StructuredTetMesh.XMin);
        uniform.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -500));
        var flat = StructuralSolver.Solve(uniform);

        double weighted = flat.MaxVonMises;
        flat.Averaging = NodalAveraging.Unweighted;
        double unweighted = flat.MaxVonMises;
        output.WriteLine($"uniform mesh: weighted {weighted:G12}, unweighted {unweighted:G12}");
        Assert.Equal(weighted, unweighted, Math.Abs(weighted) * 1e-12);

        // Setting the same value again must not invalidate the cache; setting a different
        // one must.
        var graded = new StructuralModel(
            AnalysisMesh.Of(StructuredTetMesh.PlateWithHole(60, 20, 2, 5, 32, 4, 1)), Steel);
        for (int node = 0; node < graded.Mesh.NodeCount; node++)
            graded.FixNode(node, Dof.Z);
        graded.Fix(StructuredTetMesh.XMin, Dof.X | Dof.Y);
        graded.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(20_000, 0, 0));
        var results = StructuralSolver.Solve(graded);

        var first = results.NodalStress;
        Assert.Same(first, results.NodalStress);          // cached
        results.Averaging = NodalAveraging.VolumeWeighted; // same value: no invalidation
        Assert.Same(first, results.NodalStress);

        results.Averaging = NodalAveraging.Unweighted;
        Assert.NotSame(first, results.NodalStress);
        double gradedWeighted = 0;
        for (int v = 0; v < first.Count; v++)
            gradedWeighted = Math.Max(gradedWeighted, TetElementVonMises(first[v]));
        output.WriteLine(
            $"graded mesh: weighted {gradedWeighted:G8}, unweighted {results.MaxVonMises:G8}");
        Assert.NotEqual(gradedWeighted, results.MaxVonMises, 6);
    }

    private static double TetElementVonMises(SymmetricTensor3 s)
    {
        double dxy = s.Xx - s.Yy, dyz = s.Yy - s.Zz, dzx = s.Zz - s.Xx;
        return Math.Sqrt(
            0.5 * (dxy * dxy + dyz * dyz + dzx * dzx)
            + 3.0 * (s.Xy * s.Xy + s.Yz * s.Yz + s.Xz * s.Xz));
    }

    // ---- the sampling fallback -----------------------------------------------------

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void SamplingOntoAForeignTessellation_ReproducesALinearFieldExactly(ElementOrder order)
    {
        // The branch the "same mesh" case never reaches: a display mesh whose vertices are
        // mostly NOT analysis nodes, so the barycentric fallback and the facets' own shape
        // functions do the work. Ground truth is analytic — the solution here is the
        // uniform uniaxial state, whose displacement is LINEAR, and a facet of either
        // order reproduces a linear field exactly. An interpolation that paired the wrong
        // shape function with the wrong node would not.
        const double sigma = 30.0;
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 6, 4, 2);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);

        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin, Dof.X);
        model.FixNode(FeaEquilibriumTests.Node(mesh, Vector3d.Zero), Dof.Y | Dof.Z);
        model.FixNode(FeaEquilibriumTests.Node(mesh, new Vector3d(0, Size.Y, 0)), Dof.Z);
        model.Traction(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(sigma, 0, 0));
        var results = StructuralSolver.Solve(model);

        // A DIFFERENT division of the same box: same surface, different vertices.
        var display = StructuredTetMesh.Box(Vector3d.Zero, Size, 5, 3, 3).BoundaryMesh(out _);
        var fields = results.SampleOnto(display, out double distance);
        var sampled = fields.Single(f => f.Name == StructuralResults.FieldNames.Displacement);

        double nu = Steel.PoissonsRatio, e0 = Steel.YoungsModulus;
        int interpolated = 0;
        double worst = 0;
        var analysisPositions = new HashSet<Vector3d>();
        for (int node = 0; node < mesh.NodeCount; node++)
            analysisPositions.Add(mesh.Position(node));

        for (int v = 0; v < display.VertexCount; v++)
        {
            var p = display.GetPosition(v);
            if (!analysisPositions.Contains(p))
                interpolated++;
            var exact = new Vector3d(sigma / e0 * p.X, -nu * sigma / e0 * p.Y, -nu * sigma / e0 * p.Z);
            worst = Math.Max(worst, (sampled.VectorAt(v) - exact).Length);
        }
        double scale = sigma / e0 * Size.X;

        output.WriteLine(
            $"{order}: {display.VertexCount} display vertices, {interpolated} of them "
            + $"interpolated rather than matched; max sampling distance {distance:E3}; "
            + $"worst displacement error {worst:E3} of {scale:E3} (relative {worst / scale:E3})");

        Assert.True(interpolated > display.VertexCount / 2,
            $"only {interpolated} of {display.VertexCount} vertices exercised the fallback");
        Assert.True(distance <= 1e-9 * Size.Length, $"sampling distance {distance:E3}");
        Assert.True(worst <= 1e-9 * scale, $"interpolated displacement error {worst:E3}");
    }
}
