using EngrCAD.Core;
using EngrCAD.Fea;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// Thermal topology optimisation: the structural SIMP loop with the density scaling the
/// CONDUCTANCE instead of the stiffness — verified in the landed optimiser's own style
/// (the p = 1 uniform closed form, the finite-difference sensitivity through the
/// production evaluator, the two-constructions compliance identity, and the volume-to-point
/// behaviour asserted as a measured improvement over the uniform design).
/// </summary>
public class ThermalTopologyTests(ITestOutputHelper output)
{
    private const double Conductivity = 200;                 // W/(m·K), numerically mW/(mm·K)

    /// <summary>The thermal twin of the structural bar: a whole face held at ZERO, a total
    /// heat load on the far face — a uniform axial flux, so the field's gradient is uniform
    /// and the uniform density is a stationary point at every penalty.</summary>
    private static ThermalModel Bar(double power = 1000, int nx = 8)
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero,
            new Vector3d(TopologyFixtures.BarLength, TopologyFixtures.BarSide, TopologyFixtures.BarSide),
            nx, 2, 2);
        var material = TopologyFixtures.Poissonless(1).WithThermal(Conductivity, 500e6);
        var model = new ThermalModel(AnalysisMesh.Of(tets), material);
        model.Temperature(Facets.Tag(StructuredTetMesh.XMin), 0);
        model.HeatLoad(Facets.Tag(StructuredTetMesh.XMax), power);
        return model;
    }

    /// <summary><b>The p = 1 / p = 3 uniform closed form, the structural test's thermal
    /// twin</b>: full-density compliance is Q·ΔT = Q²·L/(k·A) exactly, and the uniform
    /// field scales it by 1/f^p.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void UniformBar_MatchesTheClosedFormCompliance(double penalty)
    {
        const double fraction = 0.5, power = 1000;
        var model = Bar(power);
        double solid = power * power * TopologyFixtures.BarLength
            / (Conductivity * TopologyFixtures.BarSide * TopologyFixtures.BarSide);

        var result = TopologyOptimizer.MinimizeThermal(model, new TopologyOptions
        {
            VolumeFraction = fraction,
            FilterRadius = 6.0,
            Penalty = penalty,
        });

        Assert.Null(result.Model);
        Assert.Same(model, result.ThermalModel);
        foreach (double density in result.Density)
            Assert.Equal(fraction, density, 12);
        double expected = solid / Math.Pow(fraction, penalty);
        output.WriteLine(
            $"p={penalty}: c = {result.Compliance:G10}, closed form {expected:G10}, "
            + $"ratio {result.Compliance / expected:G10}");
        Assert.Equal(expected, result.Compliance, 6);
    }

    /// <summary>The gradient against a central difference, THROUGH the production
    /// evaluator — unfiltered and through the density filter's chain rule.</summary>
    [Theory]
    [InlineData(TopologyFilter.None)]
    [InlineData(TopologyFilter.Density)]
    public void Sensitivity_MatchesAFiniteDifference(TopologyFilter filter)
    {
        var model = Bar(nx: 6);
        var options = new TopologyOptions
        {
            VolumeFraction = 0.5,
            FilterRadius = 8.0,
            Penalty = 3.0,
            Filter = filter,
        };
        var (evaluator, _) = TopologyOptimizer.BuildThermalEvaluator(model, options);
        int count = model.Mesh.ElementCount;
        var design = new double[count];
        for (int e = 0; e < count; e++)
            design[e] = 0.3 + 0.5 * ((e * 29) % 13) / 12.0;

        evaluator.Evaluate(design);
        var analytic = (double[])evaluator.DesignSensitivity.Clone();

        double worst = 0;
        for (int probe = 0; probe < count; probe += Math.Max(1, count / 7))
        {
            const double delta = 1e-6;
            double keep = design[probe];
            design[probe] = keep + delta;
            double up = evaluator.Evaluate(design);
            design[probe] = keep - delta;
            double down = evaluator.Evaluate(design);
            design[probe] = keep;
            double numeric = (up - down) / (2 * delta);
            worst = Math.Max(worst, Math.Abs(numeric - analytic[probe]) / Math.Abs(numeric));
        }
        output.WriteLine($"{filter}: worst relative difference {worst:G4}");
        Assert.True(worst < 1e-5, $"worst relative difference {worst:G4}");
    }

    /// <summary><c>f'T</c> is the definition; <c>sum rho^p·(T_e' k0 T_e)</c> is the sum the
    /// sensitivity is built from. Two constructions checking each other.</summary>
    [Fact]
    public void Compliance_AgreesWithTheEnergySumTheSensitivityUses()
    {
        var model = Bar();
        var options = new TopologyOptions
        {
            VolumeFraction = 0.5,
            FilterRadius = 6.0,
            Filter = TopologyFilter.None,
        };
        var (evaluator, _) = TopologyOptimizer.BuildThermalEvaluator(model, options);
        var design = new double[model.Mesh.ElementCount];
        for (int e = 0; e < design.Length; e++)
            design[e] = 0.2 + 0.6 * ((e * 17) % 11) / 10.0;

        double byDefinition = evaluator.Evaluate(design);
        double byEnergies = evaluator.ComplianceFromEnergies();
        output.WriteLine($"f'T = {byDefinition:G12}, energy sum = {byEnergies:G12}");
        Assert.Equal(byDefinition, byEnergies, byDefinition * 1e-11);
    }

    /// <summary><b>The volume-to-point behaviour</b>: uniform generation drained to one cold
    /// edge through a 30% budget of conductor — the optimised layout must beat the uniform
    /// design measurably (the assertion with teeth), never rise, and hold the volume.</summary>
    [Fact]
    public void VolumeToPoint_BeatsTheUniformDesign()
    {
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(40, 40, 5), 10, 10, 1);
        var material = TopologyFixtures.Poissonless(1).WithThermal(Conductivity, 500e6);
        var model = new ThermalModel(AnalysisMesh.Of(tets), material);
        model.Temperature(Facets.Tag(StructuredTetMesh.XMin), 0);
        model.Generation(0.5);

        var result = TopologyOptimizer.MinimizeThermal(model, new TopologyOptions
        {
            VolumeFraction = 0.3,
            FilterRadius = 5.0,
            MaxIterations = 80,
        });

        double uniform = result.History[0].Compliance;       // the seed IS the uniform design
        double optimised = result.Compliance;
        output.WriteLine(
            $"uniform {uniform:G6} -> optimised {optimised:G6} "
            + $"({optimised / uniform:P1}) in {result.Iterations} iterations");
        Assert.True(optimised < 0.9 * uniform,
            "the dendrite must beat the uniform smear by a measurable margin");
        for (int i = 1; i < result.History.Count; i++)
            Assert.True(result.History[i].Compliance <= result.History[i - 1].Compliance + 1e-9,
                $"compliance rose at iteration {result.History[i].Number}");
        Assert.Equal(0.3, result.History[^1].VolumeFraction, 10);
    }

    [Fact]
    public void TheRefusals_NameTheirReasons()
    {
        var options = new TopologyOptions { VolumeFraction = 0.3, FilterRadius = 6.0 };

        // Convection: a film on an evolving boundary is a design-dependent load.
        var convective = Bar();
        convective.Convection(Facets.Tag(StructuredTetMesh.XMax), 0.02, 20);
        Assert.Contains("self-adjoint", Assert.Throws<FeaException>(() =>
            TopologyOptimizer.MinimizeThermal(convective, options)).Message);

        // A NONZERO prescribed temperature: the K_fc coupling moves with the design.
        var tets = StructuredTetMesh.Box(
            Vector3d.Zero,
            new Vector3d(TopologyFixtures.BarLength, TopologyFixtures.BarSide, TopologyFixtures.BarSide),
            8, 2, 2);
        var hot = new ThermalModel(AnalysisMesh.Of(tets),
            TopologyFixtures.Poissonless(1).WithThermal(Conductivity, 500e6));
        hot.Temperature(Facets.Tag(StructuredTetMesh.XMin), 25);
        hot.HeatLoad(Facets.Tag(StructuredTetMesh.XMax), 100);
        Assert.Contains("volume-to-point", Assert.Throws<FeaException>(() =>
            TopologyOptimizer.MinimizeThermal(hot, options)).Message);

        // No sink at all: nothing drives the field.
        var adrift = new ThermalModel(AnalysisMesh.Of(tets),
            TopologyFixtures.Poissonless(1).WithThermal(Conductivity, 500e6));
        adrift.Generation(1);
        Assert.Contains("sink", Assert.Throws<FeaException>(() =>
            TopologyOptimizer.MinimizeThermal(adrift, options)).Message);
    }
}
