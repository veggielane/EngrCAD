using EngrCAD.Core;
using EngrCAD.Fea;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The heatsink correlations, held the ⚠-transcription way (constants asserted in
/// datasheet form; the classic Nu = 1.31 DERIVED from the composite rather than stored)
/// plus the two-route verifications no spreadsheet tool has: the fin-efficiency closed
/// form against an independent finite-difference solve of the 1D fin equation, and — the
/// discriminating row — against a real 3D conduction solve of the very fin, through the
/// landed thermal solver's own Convection film BCs.
/// </summary>
public class NaturalConvectionTests(ITestOutputHelper output)
{
    [Fact]
    public void TheTranscriptions_AreAssertedInDatasheetForm()
    {
        // ⚠ dry air at 300 K (Incropera) and the Bar-Cohen & Rohsenow optimum — the value
        // IS the transcription; a re-typed formula agrees with its own mistake.
        Assert.Equal(0.0263, NaturalConvection.AirConductivity);
        Assert.Equal(1.589e-5, NaturalConvection.AirKinematicViscosity);
        Assert.Equal(2.25e-5, NaturalConvection.AirThermalDiffusivity);
        Assert.Equal(54.3, NaturalConvection.ElenbaasOptimum);

        // The classic Nu = 1.31 at the optimum is DERIVED from the composite — a second
        // stored copy could only drift, so the assertion is that the composite yields it.
        Assert.Equal(1.307, NaturalConvection.Nusselt(NaturalConvection.ElenbaasOptimum), 3);
    }

    [Fact]
    public void TheOptimumSpacing_CarriesItsOwnQuarterPowerScaling()
    {
        // S ∝ ΔT^(−1/4) at a fixed film temperature term — the beta shift makes it only
        // approximate through the ambient, so pin the EXACT identity by comparing at the
        // same beta: scale the rise 16× and the ambient so the film temperature's beta
        // compensates... simpler and exact: the closed form itself, restated with shared
        // beta, halves. Here the honest check is monotonic + the near-quarter-power band.
        double s1 = NaturalConvection.OptimumSpacing(0.1, 5);
        double s16 = NaturalConvection.OptimumSpacing(0.1, 80);
        output.WriteLine($"S(5K) = {s1 * 1000:0.##} mm, S(80K) = {s16 * 1000:0.##} mm, "
            + $"ratio {s1 / s16:0.###} (quarter-power says 2.0 at fixed beta)");
        Assert.True(s16 < s1);
        Assert.InRange(s1 / s16, 1.9, 2.1);                  // beta drift stays small

        // And the spacing GROWS with channel length at the quarter power exactly (L is
        // outside the beta term): S(16L) = 2·S(L) bit-tight.
        double sL = NaturalConvection.OptimumSpacing(0.1, 10);
        double s16L = NaturalConvection.OptimumSpacing(1.6, 10);
        Assert.Equal(2.0, s16L / sL, 12);
    }

    [Fact]
    public void FinEfficiency_MatchesAnIndependentFiniteDifferenceSolve()
    {
        // The 1D fin equation d²θ/dx² = m²θ, θ(0) = 1, θ'(H) = 0, solved by central
        // differences — sharing no line with tanh(mH)/(mH). Efficiency = mean(θ).
        const double h = 12, k = 200, t = 1.5e-3, H = 0.04;
        double closed = NaturalConvection.FinEfficiency(h, k, t, H);

        const int n = 4000;
        double dx = H / n;
        double m2 = 2 * h / (k * t);
        // Thomas solve of the tridiagonal system for interior nodes 1..n (node n has the
        // adiabatic mirror θ_{n+1} = θ_{n−1}).
        var theta = new double[n + 1];
        var a = new double[n + 1];
        var b = new double[n + 1];
        var c = new double[n + 1];
        var d = new double[n + 1];
        for (int i = 1; i <= n; i++)
        {
            a[i] = 1;
            b[i] = -(2 + m2 * dx * dx);
            c[i] = 1;
            d[i] = 0;
        }
        d[1] -= 1.0;                                         // θ(0) = 1
        c[n - 1] = 1;                                        // ordinary interior row
        a[n] = 2;                                            // adiabatic mirror at the tip
        for (int i = 2; i <= n; i++)
        {
            double w = a[i] / b[i - 1];
            b[i] -= w * c[i - 1];
            d[i] -= w * d[i - 1];
        }
        theta[n] = d[n] / b[n];
        for (int i = n - 1; i >= 1; i--)
            theta[i] = (d[i] - c[i] * theta[i + 1]) / b[i];
        theta[0] = 1;
        // η = (1/H)·∫θ dx (heat in = ∫2h·θ over the surface; ideal fin has θ ≡ 1).
        double sum = 0;
        for (int i = 0; i < n; i++)
            sum += 0.5 * (theta[i] + theta[i + 1]) * dx;
        double numeric = sum / H;

        output.WriteLine($"closed form {closed:G8}, finite difference {numeric:G8}");
        Assert.Equal(closed, numeric, 6);
        // And the limit: a fin that convects nothing is perfectly efficient.
        Assert.Equal(1.0, NaturalConvection.FinEfficiency(1e-9, k, t, H), 6);
    }

    /// <summary>THE discriminating row: the 1D closed form against a real 3D conduction
    /// solve of the same fin through the landed thermal solver — base held at the rise,
    /// the film on the two faces, the tip left adiabatic so both constructions describe
    /// one fin. The heat drawn at the base must equal η·h·A·ΔT.</summary>
    [Fact]
    public void OneFin_FeaAgreesWithTheClosedForm()
    {
        const double rise = 40;                              // K above ambient (field = rise)
        const double hSi = 12;                               // W/(m²·K)
        const double conductivity = 200;                     // W/(m·K)
        const double thickness = 1.5, height = 40, length = 60;   // mm

        var tets = StructuredTetMesh.Box(
            Vector3d.Zero, new Vector3d(length, thickness, height), 12, 1, 10);
        var material = TopologyFixtures.Poissonless(1).WithThermal(conductivity, 500e6);
        var model = new ThermalModel(AnalysisMesh.Of(tets), material);
        model.Temperature(Facets.Tag(StructuredTetMesh.ZMin), rise);
        // The film on the two big faces only (the tip stays adiabatic, matching the
        // closed form's own boundary condition); ambient 0, so the field IS the rise.
        // W/(m²·K) -> mW/(mm²·K) is ×1e-3, the PcbThermal conversion.
        model.Convection(Facets.Tag(StructuredTetMesh.YMin), hSi * 1e-3, 0);
        model.Convection(Facets.Tag(StructuredTetMesh.YMax), hSi * 1e-3, 0);

        var results = ThermalSolver.Solve(model);
        double feaWatts = results.Report.ConvectiveHeat / 1000;   // mW -> W

        double eta = NaturalConvection.FinEfficiency(
            hSi, conductivity, thickness / 1000, height / 1000);
        double closedWatts = eta * hSi
            * (2 * (length / 1000) * (height / 1000)) * rise;

        output.WriteLine(
            $"eta = {eta:0.####}; closed form {closedWatts:G6} W, FEA {feaWatts:G6} W, "
            + $"ratio {feaWatts / closedWatts:0.####}");
        Assert.Equal(closedWatts, feaWatts, closedWatts * 0.02);
    }

    [Fact]
    public void Sizing_MeetsTheRise_AndRefusesAnImpossibleEnvelopeByName()
    {
        var spec = new HeatsinkSpec(
            PowerWatts: 12, AllowableRise: 35, BaseWidth: 80, BaseDepth: 80,
            MaxFinHeight: 40);
        var design = HeatsinkSizing.Size(spec);
        output.WriteLine(
            $"{design.FinCount} fins at {design.FinSpacing:0.##} mm spacing, "
            + $"height {design.FinHeight:0.#} mm, h = {design.FilmCoefficient:0.##}, "
            + $"eta = {design.FinEfficiency:0.###}, R = {design.ThermalResistance:0.###} K/W, "
            + $"rise {design.PredictedRise:0.#} K");
        Assert.True(design.PredictedRise <= spec.AllowableRise + 1e-9);
        Assert.True(design.FinCount >= 2);
        // The array fits the stated width.
        Assert.True(design.FinCount * spec.FinThickness
            + (design.FinCount - 1) * design.FinSpacing <= spec.BaseWidth + design.FinSpacing);

        // An envelope that cannot carry the power refuses naming BOTH numbers.
        var impossible = spec with { PowerWatts = 500, MaxFinHeight = 10 };
        var refusal = Assert.Throws<FeaException>(() => HeatsinkSizing.Size(impossible));
        Assert.Contains("500", refusal.Message);
        Assert.Contains("short by", refusal.Message);
    }
}
