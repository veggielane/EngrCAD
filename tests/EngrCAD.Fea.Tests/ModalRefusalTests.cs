using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// What a modal solve refuses, and whether the message names the way out. Every refusal here
/// covers a mistake that would otherwise produce a plausible-looking wrong answer rather
/// than an obvious failure.
/// </summary>
public class ModalRefusalTests
{
    private static AnalysisMesh SmallBeam(ElementOrder order) =>
        ModalFixtures.Beam(40, 10, 10, 4, 1, 1, order);

    [Fact]
    public void RowSumLumping_IsRefusedForQuadraticElements_ByName()
    {
        var model = new StructuralModel(SmallBeam(ElementOrder.Quadratic), ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));

        var ex = Assert.Throws<FeaException>(() => ModalSolver.Solve(
            model, new ModalSolveOptions { Lumping = MassLumping.RowSum }));

        // The number, the reason, and the alternative.
        Assert.Contains("-V/20", ex.Message);
        Assert.Contains("NEGATIVE mass", ex.Message);
        Assert.Contains("MassLumping.Hrz", ex.Message);
    }

    [Fact]
    public void RowSumLumping_IsAllowedForLinearElements()
    {
        // The refusal is about the ELEMENT ORDER, not about the scheme: a 4-node
        // tetrahedron's row sums are rho·V/4 at every node, all positive.
        var model = new StructuralModel(SmallBeam(ElementOrder.Linear), ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        var results = ModalSolver.Solve(
            model, new ModalSolveOptions { ModeCount = 1, Lumping = MassLumping.RowSum });
        Assert.True(results.Mode(1).Frequency > 0);
    }

    [Fact]
    public void AMaterialWithNoDensity_IsRefusedByName()
    {
        // A zero density is LEGAL for a static solve (gravity does nothing), so this mistake
        // survives every check until the eigenproblem, where it means there is no problem to
        // solve at all.
        var weightless = new Material("weightless", 210_000, 0.3);
        var model = new StructuralModel(SmallBeam(ElementOrder.Linear), weightless);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));

        var ex = Assert.Throws<FeaException>(() => ModalSolver.Solve(model));
        Assert.Contains("weightless", ex.Message);
        Assert.Contains("WithDensity", ex.Message);
        // The unit trap named, because it is the one people actually hit.
        Assert.Contains("7.85e-9", ex.Message);
    }

    [Fact]
    public void APrescribedDisplacement_IsRefusedByName()
    {
        var model = new StructuralModel(SmallBeam(ElementOrder.Linear), ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        model.Prescribe(
            Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0, 0, 0.5), Dof.Z);

        var ex = Assert.Throws<FeaException>(() => ModalSolver.Solve(model));
        Assert.Contains("homogeneous", ex.Message);
        Assert.Contains("silently ignored", ex.Message);
    }

    [Fact]
    public void AFullyRestrainedModel_IsRefusedByName()
    {
        var mesh = SmallBeam(ElementOrder.Linear);
        var model = new StructuralModel(mesh, ModalFixtures.Steel);
        for (int v = 0; v < mesh.NodeCount; v++)
            model.FixNode(v);

        var ex = Assert.Throws<FeaException>(() => ModalSolver.Solve(model));
        Assert.Contains("no modes to find", ex.Message);
    }

    [Fact]
    public void ModeCountAndToleranceAreValidatedAtTheOption()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModalSolveOptions { ModeCount = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModalSolveOptions { Tolerance = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModalSolveOptions { MaxRestarts = -1 });
    }

    [Fact]
    public void ModeNumbersAreOneBased_AndOutOfRangeSaysSo()
    {
        var model = new StructuralModel(SmallBeam(ElementOrder.Linear), ModalFixtures.Steel);
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        var results = ModalSolver.Solve(model, new ModalSolveOptions { ModeCount = 2 });

        Assert.Equal(1, results.Mode(1).Number);
        Assert.Throws<ArgumentOutOfRangeException>(() => results.Mode(0));
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => results.Mode(3));
        Assert.Contains("numbered 1 to 2", ex.Message);
    }

    [Fact]
    public void ASliverElement_IsRefusedByTheSharedJacobianGuard()
    {
        // The SAME guard both other solvers ask, with the same message — asked here rather
        // than restated, so a mesh either works for all three physics or fails identically
        // in all three. The four points are the ones FeaRefusalTests uses, verbatim from a
        // tetrahedron the Delaunay mesher really produced: exactly positive by the EXACT
        // predicate and exactly 0.0 in double precision.
        Vector3d[] corners =
        [
            new(96.875, 9.375, 0),
            new(93.86837301508133, 8.90625, 0.6370888425630207),
            new(96.875, 10, 0.625),
            new(93.86837301508133, 9.36291115743698, 1.09375),
        ];
        var model = new StructuralModel(
            AnalysisMesh.Of(StructuredTetMesh.SingleTet(corners)), ModalFixtures.Steel);

        var ex = Assert.Throws<FeaException>(() => ModalSolver.Solve(model));
        Assert.Contains("1 of 1 elements have a non-positive Jacobian", ex.Message);
        Assert.Contains("stiffness", ex.Message);
    }
}
