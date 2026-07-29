using EngrCAD.Core;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// What the transient solver refuses, and — just as deliberately — what it does NOT refuse.
/// Every message is asserted to name the thing that is wrong, because a refusal whose text
/// does not say which input to change is only a slower failure.
/// </summary>
public class TransientRefusalTests(ITestOutputHelper output)
{
    private static StructuralModel Beam()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 4, 1, 1);
        return new StructuralModel(AnalysisMesh.Of(tets), Materials.Steel)
            .Fix(Facets.Tag(StructuredTetMesh.XMin));
    }

    [Fact]
    public void ANegativelyDampedGamma_IsRefusedByName()
    {
        // gamma < 1/2 grows the amplitude at EVERY step size, so it is not a trade.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TimeIntegration.Newmark(0.25, 0.4));
        output.WriteLine(ex.Message);
        Assert.Contains("NEGATIVE numerical damping", ex.Message);
        Assert.Contains("gamma >= 1/2", ex.Message);
    }

    [Fact]
    public void AConditionallyStableMember_IsRefusedWithItsReason()
    {
        // Central difference (beta = 0) and linear acceleration (beta = 1/6) are legitimate
        // schemes; what this solver cannot do is tell the caller whether their step is inside
        // the stability limit, because that needs the largest eigenvalue of the discrete
        // system. Refusing beats returning an answer that silently explodes.
        foreach (double beta in new[] { 0.0, 1.0 / 6.0 })
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => TimeIntegration.Newmark(beta, 0.5));
            output.WriteLine($"beta {beta:G4}: {ex.Message}");
            Assert.Contains("unconditionally stable only for 2·beta >= gamma", ex.Message);
            Assert.Contains("largest eigenvalue", ex.Message);
            // The message names the smallest beta that would be accepted.
            Assert.Contains("0.25", ex.Message);
        }
    }

    [Fact]
    public void ExplicitIntegration_IsRefusedWithTheStructuralReason()
    {
        var ex = Assert.Throws<NotSupportedException>(() => _ = TimeIntegration.CentralDifference);
        output.WriteLine(ex.Message);
        // The reason is not "not implemented" - it is that there is no diagonal mass matrix
        // to make it pay, because row-sum lumping is itself refused for quadratic elements.
        Assert.Contains("DIAGONAL mass matrix", ex.Message);
        Assert.Contains("-V/20", ex.Message);
        Assert.Contains("conditionally stable", ex.Message);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(-0.5)]
    public void AnHhtAlphaOutsideItsRange_IsRefused(double alpha)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TimeIntegration.HilberHughesTaylor(alpha));
        output.WriteLine($"alpha {alpha}: {ex.Message}");
        Assert.Contains("[-1/3, 0]", ex.Message);
    }

    [Fact]
    public void AnUnreachableSpectralRadius_IsRefusedNamingTheFamilyThatWouldReachIt()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => TimeIntegration.ForSpectralRadius(0.2));
        output.WriteLine(ex.Message);
        Assert.Contains("[1/2, 1]", ex.Message);
        Assert.Contains("generalized-alpha", ex.Message);
    }

    [Fact]
    public void AMaterialWithNoDensity_IsRefusedByName()
    {
        // The same guard the modal solver uses, with the consequence stated for THIS analysis.
        var weightless = new Material("weightless", 210_000, 0.3);
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 4, 1, 1);
        var model = new StructuralModel(AnalysisMesh.Of(tets), weightless)
            .Fix(Facets.Tag(StructuredTetMesh.XMin));

        var ex = Assert.Throws<FeaException>(
            () => TransientSolver.Solve(model, new TransientSolveOptions(1e-6, 10)));
        output.WriteLine(ex.Message);
        Assert.Contains("weightless", ex.Message);
        Assert.Contains("WithDensity", ex.Message);
        Assert.Contains("7.85e-9", ex.Message);
        // And it names the transient's own consequence, not the modal one.
        Assert.Contains("M·a + C·v + K·u = f(t)", ex.Message);
    }

    [Fact]
    public void AnInitialStateOfTheWrongLength_IsRefusedByName()
    {
        var model = Beam();
        var ex = Assert.Throws<FeaException>(
            () => TransientSolver.Solve(
                model,
                new TransientSolveOptions(1e-6, 10)
                {
                    InitialVelocity = new Vector3d[model.Mesh.NodeCount - 1],
                }));
        output.WriteLine(ex.Message);
        Assert.Contains(nameof(TransientSolveOptions.InitialVelocity), ex.Message);
        Assert.Contains("per NODE", ex.Message);
        Assert.Contains("mid-edge", ex.Message);
    }

    [Fact]
    public void AFullyRestrainedModel_IsRefused()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 4, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Materials.Steel);
        for (int node = 0; node < mesh.NodeCount; node++)
            model.FixNode(node);

        var ex = Assert.Throws<FeaException>(
            () => TransientSolver.Solve(model, new TransientSolveOptions(1e-6, 10)));
        output.WriteLine(ex.Message);
        Assert.Contains("nothing to step", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1e-9)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void ABadTimeStep_IsRefused(double step)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransientSolveOptions(step, 10));
    }

    [Fact]
    public void ANonPositiveStepCount_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TransientSolveOptions(1e-6, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TransientSolveOptions(1e-6, 10) { StoreEvery = 0 });
    }

    [Fact]
    public void AnUnrestrainedBodyIsNotRefused_AndThatIsTheDecision()
    {
        // The deliberate NON-refusal, pinned so it cannot be "fixed" by someone copying the
        // static solver's guard across. K alone is singular for a free body; the effective
        // stiffness carries a0·M with a0 = 1/(beta·dt²) > 0 and a consistent mass matrix is
        // positive definite, so the stepping matrix is positive definite whatever the supports
        // do. A free body under a transient load flies away, which is the answer.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 4, 1, 1);
        var model = new StructuralModel(AnalysisMesh.Of(tets), Materials.Steel)
            .Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(500, 0, 0));

        Assert.Throws<FeaException>(() => StructuralSolver.Solve(model));
        var results = TransientSolver.Solve(model, new TransientSolveOptions(1e-6, 20));
        output.WriteLine(results.Report.ToText());
        Assert.True(results.Final.MaxDisplacement > 0);
    }

    [Fact]
    public void APrescribedDisplacementIsHeld_NotScaledByTheLoadHistory()
    {
        // A support that has been moved stays moved, whatever the load history does - stated
        // in the API and pinned here, because the alternative reading (a prescribed value is
        // a "load" and scales with the factor) would look identical at factor 1 and be wrong
        // everywhere else.
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 4, 1, 1);
        var mesh = AnalysisMesh.Of(tets);
        var model = new StructuralModel(mesh, Materials.Steel)
            .Fix(Facets.Tag(StructuredTetMesh.XMin))
            .Prescribe(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0.05, 0, 0), Dof.X);

        var results = TransientSolver.Solve(
            model,
            // A load history that is exactly zero everywhere: nothing scales, so whatever
            // moves is the support's doing.
            new TransientSolveOptions(2e-7, 40) { LoadFactor = _ => 0.0 });

        foreach (var state in new[] { results.Initial, results.States[10], results.Final })
        {
            double held = 0;
            for (int node = 0; node < mesh.NodeCount; node++)
            {
                if (model.RestraintOf(node).HasFlag(Dof.X)
                    && model.PrescribedOf(node).X != 0)
                {
                    held = Math.Max(held, Math.Abs(state.DisplacementAt(node).X - 0.05));
                }
            }
            output.WriteLine($"t = {state.Time:G6}: worst deviation from the held 0.05 is {held:E3}");
            Assert.Equal(0.0, held, 1e-15);
        }
    }
}
