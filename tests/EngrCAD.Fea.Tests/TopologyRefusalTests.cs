using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Every refusal, SHOWN TO FIRE — a guard whose only evidence is that the right answer still
/// works has not been tested.
///
/// <para>The design-dependent-load refusals are the ones that matter: each names a case where
/// the strain-energy sensitivity is not merely inaccurate but WRONG, so a run that proceeded
/// would return a converged, plausible, self-consistent answer to a question nobody
/// asked.</para>
/// </summary>
public sealed class TopologyRefusalTests
{
    private static TopologyOptions Usable => new() { VolumeFraction = 0.5, FilterRadius = 6.0 };

    [Fact]
    public void GravityIsRefusedBecauseSelfWeightIsDesignDependent()
    {
        var model = TopologyFixtures.Bar();
        model.Gravity(new Vector3d(0, 0, -9806.65));
        var error = Assert.Throws<FeaException>(
            () => TopologyOptimizer.Minimize(model, Usable));
        Assert.Contains("design-dependent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("self-adjoint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABodyForceIsRefusedForTheSameReason()
    {
        var model = TopologyFixtures.Bar();
        model.BodyForce(_ => new Vector3d(0, 0, -1e-4));
        Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(model, Usable));
    }

    /// <summary>The refusal has to be reachable again after the load is withdrawn, or
    /// <c>ClearLoads</c> would leave a model permanently un-optimisable.</summary>
    [Fact]
    public void ClearingTheLoadsWithdrawsTheRefusal()
    {
        var model = TopologyFixtures.Bar();
        model.Gravity(new Vector3d(0, 0, -9806.65));
        Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(model, Usable));
        model.ClearLoads();
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(1000, 0, 0));
        var result = TopologyOptimizer.Minimize(model, Usable with { MaxIterations = 3 });
        Assert.Equal(0.5, result.VolumeFraction, 12);
    }

    [Fact]
    public void AThermalLoadIsRefused()
    {
        // Steel rather than the Poissonless fixture material: a thermal load is refused at the
        // point of application when nothing states an expansion coefficient, so the model has
        // to be able to CARRY the load for this refusal to be the one being measured.
        var model = TopologyFixtures.Cantilever(0, out var mesh);
        var rise = new double[mesh.NodeCount];
        Array.Fill(rise, 40.0);
        model.ThermalLoad(rise, 0);
        var error = Assert.Throws<FeaException>(
            () => TopologyOptimizer.Minimize(model, Usable));
        Assert.Contains("thermal", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APrescribedDisplacementIsRefusedBecauseItFlipsTheProblemsSign()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 8, 2, 2);
        var model = new StructuralModel(
            AnalysisMesh.Of(tets), TopologyFixtures.Poissonless(70_000));
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        model.Prescribe(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(0.05, 0, 0), Dof.X);
        var error = Assert.Throws<FeaException>(
            () => TopologyOptimizer.Minimize(model, Usable));
        Assert.Contains("prescribed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A radius the mesh cannot express leaves each element alone in its own
    /// neighbourhood, so the filter is present and does nothing — the silent failure the
    /// whole feature exists to prevent.</summary>
    [Fact]
    public void AFilterRadiusSmallerThanAnElementIsRefused()
    {
        var model = TopologyFixtures.Bar();
        var error = Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(
            model, Usable with { FilterRadius = 1e-4 }));
        Assert.Contains("minimum MEMBER size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMinimumDensityAboveTheVolumeFractionIsRefused()
    {
        var model = TopologyFixtures.Bar();
        var error = Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(
            model, Usable with { VolumeFraction = 0.05, MinimumDensity = 0.1 }));
        Assert.Contains("never be met", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelWithNoLoadIsRefused()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 4, 2, 2);
        var model = new StructuralModel(
            AnalysisMesh.Of(tets), TopologyFixtures.Poissonless(70_000));
        model.Fix(Facets.Tag(StructuredTetMesh.XMin));
        var error = Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(model, Usable));
        Assert.Contains("nothing to minimise", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelWithNoSupportsIsRefused()
    {
        var tets = StructuredTetMesh.Box(Vector3d.Zero, new Vector3d(40, 10, 10), 4, 2, 2);
        var model = new StructuralModel(
            AnalysisMesh.Of(tets), TopologyFixtures.Poissonless(70_000));
        model.Force(Facets.Tag(StructuredTetMesh.XMax), new Vector3d(1000, 0, 0));
        var error = Assert.Throws<FeaException>(() => TopologyOptimizer.Minimize(model, Usable));
        Assert.Contains("no supports", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.2)]
    public void AVolumeFractionOutsideZeroToOneIsRefused(double fraction)
    {
        var model = TopologyFixtures.Bar();
        Assert.Throws<ArgumentOutOfRangeException>(() => TopologyOptimizer.Minimize(
            model, Usable with { VolumeFraction = fraction }));
    }

    [Fact]
    public void APenaltyBelowOneIsRefused()
    {
        var model = TopologyFixtures.Bar();
        Assert.Throws<ArgumentOutOfRangeException>(() => TopologyOptimizer.Minimize(
            model, Usable with { Penalty = 0.5 }));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void AThresholdOutsideZeroToOneIsRefused(double threshold)
    {
        var model = TopologyFixtures.Bar();
        var result = TopologyOptimizer.Minimize(model, Usable with { MaxIterations = 2 });
        Assert.Throws<ArgumentOutOfRangeException>(() => result.ExtractSurface(threshold));
    }

    /// <summary>A threshold no element reaches leaves nothing to bound, and the message says
    /// so rather than the caller receiving an empty mesh.</summary>
    [Fact]
    public void AThresholdNoMaterialReachesIsRefusedByName()
    {
        var model = TopologyFixtures.Bar();
        var options = Usable;
        var (_, volumes) = TopologyOptimizer.BuildEvaluator(model, options);
        var field = new double[model.Mesh.ElementCount];
        Array.Fill(field, 0.2);
        var result = new TopologyResult(
            model.Mesh, model, null, options, field, field, volumes, [], TopologyStop.Converged, 1, 1);
        var error = Assert.Throws<FeaException>(() => result.ExtractSurface(0.9));
        Assert.Contains("No material survives", error.Message, StringComparison.Ordinal);
    }
}
