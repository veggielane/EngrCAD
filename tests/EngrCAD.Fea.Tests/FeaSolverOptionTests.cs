using EngrCAD.Core;
using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The solver's own options: the two linear solvers, the two elimination orderings, the
/// condition estimate, and multi-material assembly.
/// </summary>
public class FeaSolverOptionTests(ITestOutputHelper output)
{
    private static readonly Material Steel = new("option steel", 210_000, 0.3);
    private static readonly Vector3d Size = new(40, 20, 10);

    private static StructuralModel Cantilever(ElementOrder order, int n = 3)
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 4 * n, 2 * n, n);
        var mesh = order == ElementOrder.Linear ? AnalysisMesh.Of(tets) : AnalysisMesh.Quadratic(tets);
        var model = new StructuralModel(mesh, Steel);
        model.Fix(StructuredTetMesh.XMin);
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, -4000));
        return model;
    }

    [Theory]
    [InlineData(ElementOrder.Linear, 3)]
    [InlineData(ElementOrder.Quadratic, 2)]
    public void TheTwoOrderingsSolveTheSameSystemToTheSameAnswer(ElementOrder order, int n)
    {
        // AMD is not bit-identical to natural — a different elimination order is different
        // arithmetic, which is exactly why Core keeps natural as its default. What must
        // hold is that the ANSWERS agree to solver accuracy, and that AMD carries less
        // fill, which is the whole reason to pay for the permutation.
        //
        // The quadratic case is deliberately SMALL here: at n = 3 the natural ordering
        // takes 158 s against AMD's 1.3 s (19.4 M against 1.7 M factor entries), which is
        // the headline measurement and belongs in FeaBenchmark rather than in a test
        // everybody runs.
        var natural = StructuralSolver.Solve(
            Cantilever(order, n), new StructuralSolveOptions { Ordering = SparseOrdering.Natural });
        var amd = StructuralSolver.Solve(
            Cantilever(order, n), new StructuralSolveOptions { Ordering = SparseOrdering.Amd });

        double scale = natural.MaxDisplacement;
        double worst = 0;
        for (int v = 0; v < natural.Mesh.NodeCount; v++)
            worst = Math.Max(worst, (natural.DisplacementAt(v) - amd.DisplacementAt(v)).Length);

        output.WriteLine(
            $"{order}: natural factor {natural.Report.FactorNonZeros:N0} nnz in "
            + $"{natural.Report.FactorMs:F0} ms, AMD {amd.Report.FactorNonZeros:N0} nnz in "
            + $"{amd.Report.FactorMs:F0} ms ({(double)natural.Report.FactorNonZeros / amd.Report.FactorNonZeros:F2}x "
            + $"less fill); displacement difference {worst:E3} of {scale:E3}");

        Assert.True(worst <= 1e-9 * scale, $"answers differ by {worst:E3}");
        Assert.True(amd.Report.FactorNonZeros < natural.Report.FactorNonZeros,
            $"AMD produced {amd.Report.FactorNonZeros:N0} nnz against natural's "
            + $"{natural.Report.FactorNonZeros:N0} — no fill reduction at all");
    }

    [Theory]
    [InlineData(ElementOrder.Linear)]
    [InlineData(ElementOrder.Quadratic)]
    public void ConjugateGradientsReachTheDirectSolversAnswer(ElementOrder order)
    {
        var direct = StructuralSolver.Solve(Cantilever(order));
        var iterative = StructuralSolver.Solve(Cantilever(order), new StructuralSolveOptions
        {
            Method = FeaSolveMethod.ConjugateGradient,
            Cg = new CgOptions { RelativeTolerance = 1e-12 },
        });

        double scale = direct.MaxDisplacement;
        double worst = 0;
        for (int v = 0; v < direct.Mesh.NodeCount; v++)
            worst = Math.Max(worst, (direct.DisplacementAt(v) - iterative.DisplacementAt(v)).Length);

        output.WriteLine(
            $"{order}: direct {direct.Report.FactorMs + direct.Report.SolveMs:F0} ms "
            + $"({direct.Report.FactorNonZeros:N0} factor nnz); CG "
            + $"{iterative.Report.Iterations} iterations in {iterative.Report.SolveMs:F0} ms; "
            + $"displacement difference {worst:E3} of {scale:E3} "
            + $"(relative {worst / scale:E3})");

        Assert.True(iterative.Report.Converged, iterative.Report.ToText());
        Assert.Equal(0, iterative.Report.FactorNonZeros);
        Assert.True(worst <= 1e-6 * scale, $"CG and the direct solve differ by {worst:E3}");
        // And the strain energy — a global, quadratic quantity — agrees more tightly still.
        Assert.Equal(direct.StrainEnergy, iterative.StrainEnergy, Math.Abs(direct.StrainEnergy) * 1e-8);
    }

    [Fact]
    public void TheConditionEstimateIsReportedAndRisesAsTheMeshRefines()
    {
        // A stiffness matrix's condition number grows like h^-2 for a fixed geometry, so
        // halving the element size should multiply it by roughly four. The assertion is
        // deliberately loose — this is a power-iteration ESTIMATE and says so — but a
        // number that did not move with the mesh would not be measuring the matrix.
        var options = new StructuralSolveOptions { EstimateCondition = true };
        var coarse = StructuralSolver.Solve(Cantilever(ElementOrder.Linear, 2), options);
        var fine = StructuralSolver.Solve(Cantilever(ElementOrder.Linear, 4), options);

        Assert.NotNull(coarse.Report.ConditionEstimate);
        Assert.NotNull(fine.Report.ConditionEstimate);
        double a = coarse.Report.ConditionEstimate!.Value;
        double b = fine.Report.ConditionEstimate!.Value;

        output.WriteLine(
            $"condition estimate {a:E3} at {coarse.Report.FreeDofs:N0} DOF, "
            + $"{b:E3} at {fine.Report.FreeDofs:N0} DOF (ratio {b / a:F2}, h halved)");
        output.WriteLine(coarse.Report.ToText());

        Assert.True(a > 1, $"condition estimate {a:E3} is below 1");
        Assert.True(b > a, $"refining did not raise the condition estimate: {a:E3} -> {b:E3}");
        Assert.InRange(b / a, 2.0, 8.0);

        // Without the option it is simply absent rather than a made-up value.
        Assert.Null(StructuralSolver.Solve(Cantilever(ElementOrder.Linear, 2)).Report.ConditionEstimate);
    }

    [Fact]
    public void TwoMaterialsInSeriesElongateByTheSeriesFormula()
    {
        // A bar of two materials end to end under uniform axial stress: the axial stress
        // is the same in both by equilibrium, so the elongation is
        // sigma·(L/2)·(1/E_soft + 1/E_stiff). The agreement is not exact — the two halves
        // want different Poisson contractions, which puts a local three-dimensional
        // disturbance at the interface — so the residual is reported rather than hidden.
        const double sigma = 30.0;
        var soft = new Material("soft", 70_000, 0.3);
        var stiff = new Material("stiff", 210_000, 0.3);

        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, Size, 12, 6, 3, p => p.X < Size.X * 0.5 ? 0 : 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, soft).SetMaterial(1, stiff);

        model.Fix(StructuredTetMesh.XMin, Dof.X);
        model.FixNode(FeaEquilibriumTests.Node(mesh, Vector3d.Zero), Dof.Y | Dof.Z);
        model.FixNode(FeaEquilibriumTests.Node(mesh, new Vector3d(0, Size.Y, 0)), Dof.Z);
        model.Traction(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(sigma, 0, 0));

        var results = StructuralSolver.Solve(model);

        double expected = sigma * (Size.X * 0.5) * (1 / soft.YoungsModulus + 1 / stiff.YoungsModulus);
        var tip = model.NodesOn(Facets.Tag(StructuredTetMesh.XMax));
        double measured = tip.Sum(n => results.DisplacementAt(n).X) / tip.Count;

        // Strains well away from the interface, one element in from each end.
        double softStrain = StrainNear(results, mesh, new Vector3d(Size.X * 0.15, Size.Y / 2, Size.Z / 2));
        double stiffStrain = StrainNear(results, mesh, new Vector3d(Size.X * 0.85, Size.Y / 2, Size.Z / 2));

        output.WriteLine(
            $"series bar: elongation {measured:F6} mm against the 1D formula {expected:F6} "
            + $"({(measured / expected - 1) * 100:+0.000;-0.000}%); axial strain "
            + $"{softStrain:E4} in the soft half and {stiffStrain:E4} in the stiff "
            + $"(ratio {softStrain / stiffStrain:F3}, materials differ by "
            + $"{stiff.YoungsModulus / soft.YoungsModulus:F1}x)");

        Assert.True(Math.Abs(measured / expected - 1) < 0.015,
            $"elongation {measured:F6} vs {expected:F6}");
        // The strain ratio is the modulus ratio only where the interface's disturbance has
        // decayed. These samples sit about one cross-section away, where Saint-Venant
        // leaves a few percent — measured 3.07 against 3.00 — so the bar is 5%, not the
        // round-off the rest of this suite works to.
        Assert.Equal(stiff.YoungsModulus / soft.YoungsModulus, softStrain / stiffStrain, 0.15);
        Assert.Equal(soft, model.MaterialOf(0));
        Assert.Equal(stiff, model.MaterialOf(mesh.ElementCount - 1));
    }

    [Fact]
    public void ThePrescribedDisplacementPathAgreesWithTheEquivalentTraction()
    {
        // Two spellings of one problem: pull a bar with a traction, or stretch it by the
        // displacement that traction produces. Same stress everywhere, and the second
        // exercises the K_fc column that moves a known displacement to the right-hand side.
        const double sigma = 45.0;
        var tets = StructuredTetMesh.Box(Vector3d.Zero, Size, 8, 4, 2);
        var mesh = AnalysisMesh.Of(tets);

        var byTraction = new StructuralModel(mesh, Steel);
        byTraction.Fix(StructuredTetMesh.XMin, Dof.X);
        byTraction.FixNode(FeaEquilibriumTests.Node(mesh, Vector3d.Zero), Dof.Y | Dof.Z);
        byTraction.FixNode(FeaEquilibriumTests.Node(mesh, new Vector3d(0, Size.Y, 0)), Dof.Z);
        byTraction.Traction(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(sigma, 0, 0));
        var a = StructuralSolver.Solve(byTraction);

        double stretch = sigma / Steel.YoungsModulus * Size.X;
        var byDisplacement = new StructuralModel(mesh, Steel);
        byDisplacement.Fix(StructuredTetMesh.XMin, Dof.X);
        byDisplacement.FixNode(FeaEquilibriumTests.Node(mesh, Vector3d.Zero), Dof.Y | Dof.Z);
        byDisplacement.FixNode(FeaEquilibriumTests.Node(mesh, new Vector3d(0, Size.Y, 0)), Dof.Z);
        byDisplacement.Prescribe(
            Facets.Tag(StructuredTetMesh.XMax), new Vector3d(stretch, 0, 0), Dof.X);
        var b = StructuralSolver.Solve(byDisplacement);

        double worst = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
            worst = Math.Max(worst, Math.Abs(a.ElementStress(e).Xx - b.ElementStress(e).Xx));

        // The enforced-displacement run is held at BOTH ends, so its resultant reaction is
        // zero — the two ends pull against each other. The meaningful number is the
        // reaction on ONE face, and it is the load the traction run applied.
        double area = Size.Y * Size.Z;
        double pulling = byDisplacement.NodesOn(Facets.Tag(StructuredTetMesh.XMax))
            .Sum(n => b.ReactionAt(n).X);
        double holding = byDisplacement.NodesOn(Facets.Tag(StructuredTetMesh.XMin))
            .Sum(n => b.ReactionAt(n).X);

        output.WriteLine(
            $"traction vs prescribed stretch: worst axial-stress difference {worst:E3} of "
            + $"{sigma}; the enforced-displacement run is pulled by {pulling:F4} N at the "
            + $"stretched end and held by {holding:F4} N at the fixed one, against "
            + $"sigma·A = {sigma * area:F4} N; resultant {b.Report.ReactionForce.X:E3}");

        Assert.True(worst <= 1e-9 * sigma, $"stress difference {worst:E3}");
        Assert.Equal(sigma * area, pulling, sigma * area * 1e-9);
        Assert.Equal(-sigma * area, holding, sigma * area * 1e-9);
        Assert.True(Math.Abs(b.Report.ReactionForce.X) <= 1e-9 * sigma * area,
            $"a bar held at both ends should have no resultant reaction, got {b.Report.ReactionForce.X:E3}");
        Assert.Equal(a.StrainEnergy, b.StrainEnergy, Math.Abs(a.StrainEnergy) * 1e-9);
    }

    private static double StrainNear(StructuralResults results, AnalysisMesh mesh, Vector3d point)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var nodes = mesh.Element(e);
            var centroid = (mesh.Position(nodes[0]) + mesh.Position(nodes[1])
                          + mesh.Position(nodes[2]) + mesh.Position(nodes[3])) * 0.25;
            double d = centroid.DistanceSquaredTo(point);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = e;
            }
        }
        return results.ElementStrain(best).Xx;
    }
}
