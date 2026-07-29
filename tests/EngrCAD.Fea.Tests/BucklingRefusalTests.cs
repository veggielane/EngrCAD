using EngrCAD.Core;
using EngrCAD.Core.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// What <see cref="BucklingSolver"/> refuses, and that each refusal names the actual cause.
/// Every one of these would otherwise return a plausible number.
/// </summary>
public class BucklingRefusalTests(ITestOutputHelper output)
{
    private static StructuralModel SmallColumn() =>
        BucklingFixtures.Column(ColumnEnds.FixedFree, 60.0, 6.0, 6, 1, ElementOrder.Quadratic)
            .Model;

    [Fact]
    public void ALoadFreeReferenceSolveIsRefused_BecauseKgWouldBeZero()
    {
        var (model, _) = BucklingFixtures.Column(
            ColumnEnds.FixedFree, 60.0, 6.0, 4, 1, ElementOrder.Quadratic);
        model.ClearLoads();
        var statics = StructuralSolver.Solve(model);
        Assert.Equal(0.0, statics.StrainEnergy);

        var error = Assert.Throws<FeaException>(() => BucklingSolver.Solve(statics));
        output.WriteLine(error.Message);
        Assert.Contains("no strain energy", error.Message);
        Assert.Contains("geometric stiffness", error.Message);
    }

    [Fact]
    public void AnUnrestrainedModelIsRefusedByName_ThroughTheSameCheckTheStaticSolverUses()
    {
        // It cannot even reach the buckling solver — the static solve it needs refuses first
        // — so the assertion is that the buckling path's own guard fires when handed results
        // from a model whose supports were removed afterwards, and that it says what a
        // buckling analysis in particular needs.
        var (model, _) = BucklingFixtures.Column(
            ColumnEnds.FixedFree, 60.0, 6.0, 4, 1, ElementOrder.Quadratic);
        var statics = StructuralSolver.Solve(model);

        var free = new StructuralModel(model.Mesh, BucklingFixtures.Material);
        free.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(-1000, 0, 0));
        var unrestrained = new StructuralResults(
            free, [.. statics.Displacement], [.. statics.Reactions], statics.Report);

        var error = Assert.Throws<FeaException>(() => BucklingSolver.Solve(unrestrained));
        output.WriteLine(error.Message);
        Assert.Contains("not restrained", error.Message);
        Assert.Contains("linearises about an equilibrium state", error.Message);
    }

    [Fact]
    public void AColumnInPureTensionHasNoPositiveFactor_AndTheRefusalSaysItIsThePhysics()
    {
        // The sign convention, verified from the other end: tension makes Kg positive
        // semi-definite, so -Kg is negative semi-definite, every theta is non-positive, and
        // there is no positive load factor anywhere in the spectrum. A solver with the sign
        // the wrong way round would happily return the compression answer here.
        var (model, _) = BucklingFixtures.Column(
            ColumnEnds.FixedFree, 60.0, 6.0, 4, 1, ElementOrder.Quadratic);
        model.ClearLoads();
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(1000, 0, 0));
        var statics = StructuralSolver.Solve(model);
        Assert.True(statics.ElementStress(0).Xx > 0, "the fixture is not in tension");

        var error = Assert.Throws<FeaException>(
            () => BucklingSolver.Solve(
                statics, new BucklingSolveOptions { ModeCount = 1, MaxRestarts = 1 }));
        output.WriteLine(error.Message);
        Assert.Contains("No POSITIVE buckling load factor", error.Message);
        Assert.Contains("no positive candidate ever appeared", error.Message);
        Assert.Contains("entirely in tension", error.Message);
    }

    [Fact]
    public void ANonConvergedReferenceSolveIsRefused()
    {
        var (model, _) = BucklingFixtures.Column(
            ColumnEnds.FixedFree, 60.0, 6.0, 4, 1, ElementOrder.Quadratic);
        var statics = StructuralSolver.Solve(model, new StructuralSolveOptions
        {
            Method = FeaSolveMethod.ConjugateGradient,
            Cg = new CgOptions { MaxIterations = 2 },
        });
        Assert.False(statics.Report.Converged);

        var error = Assert.Throws<FeaException>(() => BucklingSolver.Solve(statics));
        output.WriteLine(error.Message);
        Assert.Contains("did not converge", error.Message);
        Assert.Contains("not the equilibrium one", error.Message);
    }

    [Fact]
    public void ModeNumbersAreOneBasedAndOutOfRangeIsNamed()
    {
        var model = SmallColumn();
        var buckling = BucklingSolver.Solve(
            StructuralSolver.Solve(model), new BucklingSolveOptions { ModeCount = 2 });

        Assert.Equal(2, buckling.Modes.Count);
        Assert.Equal(1, buckling.Mode(1).Number);
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => buckling.Mode(3));
        Assert.Contains("numbered 1 to 2", error.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => buckling.Mode(0));
    }

    [Fact]
    public void OptionsRefuseNonsenseUpFront()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BucklingSolveOptions { ModeCount = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BucklingSolveOptions { Tolerance = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BucklingSolveOptions { MaxRestarts = -1 });
    }
}
