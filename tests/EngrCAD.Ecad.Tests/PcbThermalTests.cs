using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Per-component power solved as heat conduction through the board (<see cref="PcbThermal"/>),
/// verified against CLOSED FORMS in the FEA house style — because an ECAD thermal answer fails
/// plausibly, so a picture is not evidence.
///
/// <para>The exact ones are exact for a reason: a uniformly-dissipating board settles into a
/// PARABOLA that lives in the quadratic element space, so a correct solve reproduces it to
/// round-off; and past a localized source the profile is LINEAR, so the series-resistance rise is
/// exact for both element orders. Copper is verified by the RATIO it changes the rise by, the
/// refusals by name, and the units by a hand resistance estimate.</para>
/// </summary>
public class PcbThermalTests(ITestOutputHelper output)
{
    // A clean dielectric with a readable conductivity, so a hand check of any figure here is
    // arithmetic rather than bookkeeping. (Real FR4 is k = 0.3; the exactness of the solve does
    // not depend on the number, and Copper() below uses the real materials.)
    private static readonly Material CleanDielectric =
        new("test dielectric", density: 1.5e-9, thermalConductivity: 20.0, specificHeat: 1e9);

    // A rectangular board occupying x in [0, L], y in [0, d] — deliberately corner-anchored so a
    // fixed x = L edge with the rest insulated is a clean 1D problem.
    private static PcbBoard Rect(double length, double depth, double thickness) => new(
        [
            new Vector2d(0, 0), new Vector2d(length, 0),
            new Vector2d(length, depth), new Vector2d(0, depth),
        ], thickness);

    private static PcbLayout BareLayout(double length, double depth, double thickness) =>
        new(new Schematic("board"), Rect(length, depth, thickness));

    // A single "heater" component: one pad the size of the source strip, so its footprint box is
    // exactly [0, a] x [0, d] when placed at (a/2, d/2).
    private static PcbLayout HeaterLayout(
        double length, double depth, double thickness, double a, out string reference)
    {
        reference = "U1";
        var def = new PartDefinition("HEATER", "U",
            [new Pin("1", PinType.Passive)],
            new Footprint("heater_fp", [new Pad("1", new Vector2d(0, 0), a, depth, PadShape.Rectangular)]));
        var sch = new Schematic("board");
        sch.Add("U1", def);
        var layout = new PcbLayout(sch, Rect(length, depth, thickness));
        layout.Place("U1", a / 2, depth / 2);
        return layout;
    }

    // ---- 1. a uniformly-dissipating board matches the analytic conduction parabola ----

    /// <summary>
    /// <b>A uniformly-dissipating board to a fixed edge.</b> With uniform volumetric generation q,
    /// a fixed cold end and the rest insulated, the 1D profile is
    /// <c>T(x) = T0 + (q/2k)(L² − x²)</c> — a PARABOLA in the quadratic element space, so the solve
    /// reproduces it to round-off. It is also the units check: the board's stated watts must come
    /// out as exactly <c>P × 1000</c> mW of applied heat (a uniform generation integrates exactly).
    /// </summary>
    [Fact]
    public void UniformBoard_MatchesTheConductionParabolaExactly()
    {
        const double L = 40, d = 20, t = 1.5, T0 = 25, power = 1.0;   // 1 W
        double k = CleanDielectric.ThermalConductivity;
        double volume = L * d * t;
        double q = power * PcbThermalModel.MilliwattsPerWatt / volume;   // mW/mm^3

        double Exact(Vector3d p) => T0 + q / (2 * k) * (L * L - p.X * p.X);

        var spec = new PcbThermalSpec
        {
            Dielectric = CleanDielectric,
            BoardPower = power,
            MaxElementSize = 5,
            Boundaries =
            [
                PcbThermalBoundary.FixedTemperature(
                    "cold edge", Facets.OnPlane(new Vector3d(L, 0, 0), Vector3d.UnitX), T0),
            ],
        };

        var geom = PcbThermalModel.Build(BareLayout(L, d, t), spec);
        var results = ThermalSolver.Solve(geom.Model);

        double span = q * L * L / (2 * k);   // the peak rise
        double worst = 0;
        for (int v = 0; v < geom.Mesh.NodeCount; v++)
            worst = Math.Max(worst, Math.Abs(results.TemperatureAt(v) - Exact(geom.Mesh.Position(v))));

        double appliedWatts = results.Report.AppliedHeat / PcbThermalModel.MilliwattsPerWatt;
        output.WriteLine($"{geom.Mesh.ElementCount:N0} elements, peak rise {span:g6} K");
        output.WriteLine($"  worst |T - exact| {worst:E3} on a {span:g6} K span -> {worst / span:E3} relative");
        output.WriteLine($"  applied heat {appliedWatts:g8} W (stated {power:g6} W)");
        output.WriteLine($"  energy balance residual {results.Report.EnergyBalanceResidual:E3}");

        Assert.True(worst / span < 1e-9, $"parabola error {worst / span:E3}");
        // The units conversion: 1 W in, exactly P x 1000 mW of generation (uniform is exact).
        Assert.Equal(power * PcbThermalModel.MilliwattsPerWatt, results.Report.AppliedHeat, 6);
        Assert.True(results.Report.EnergyBalanceResidual < 1e-10);
    }

    // ---- 2. a single hot component to a cold edge: the series-resistance rise ----

    /// <summary>
    /// <b>A single hot component, series resistance.</b> A localized source at one end and a cold
    /// edge at the other: past the source the board carries all the power as a constant flux
    /// <c>Q/A</c>, so the profile is LINEAR and <c>T(x) = T0 + Q·(L − x)/(k·A)</c>, the slope being
    /// the series thermal resistance <c>R = L/(k·A)</c>.
    ///
    /// <para><b>Two accuracies, stated honestly.</b> The energy balance — all the generated heat
    /// leaves the cold edge — is an EXACT identity a correct solve satisfies to round-off. The
    /// far-field profile matching the 1D series-resistance line is a STATED accuracy, not
    /// round-off: a localized step source on an unstructured tet mesh is not exactly 1D (the
    /// discrete load varies across elements at one x), so the departure is the 3D discretization
    /// of the source, a few parts in 1e5 here.</para>
    /// </summary>
    [Fact]
    public void HotComponent_MatchesTheSeriesResistanceRise()
    {
        const double L = 40, d = 20, t = 1.5, a = 8, T0 = 25, power = 0.5;
        var layout = HeaterLayout(L, d, t, a, out string reference);
        var spec = new PcbThermalSpec
        {
            Dielectric = CleanDielectric,
            MaxElementSize = 4,
            ComponentPower = new Dictionary<string, double> { [reference] = power },
            Boundaries =
            [
                PcbThermalBoundary.FixedTemperature(
                    "cold edge", Facets.OnPlane(new Vector3d(L, 0, 0), Vector3d.UnitX), T0),
            ],
        };

        var geom = PcbThermalModel.Build(layout, spec);
        var results = ThermalSolver.Solve(geom.Model);

        double k = geom.ConductivityInPlane;
        double area = d * t;                                  // the yz cross-section
        double Q = results.Report.AppliedHeat;               // the ACTUAL applied heat (mW)
        double aRight = geom.FootprintBoxes[reference].Max.X; // the source's far edge

        // Far-field: nodes clear of the source and any element straddling its boundary.
        double margin = 2 * 4;   // ~ two element sizes past the source
        double Exact(double x) => T0 + Q * (L - x) / (k * area);
        double worst = 0, maxRise = 0;
        int checkedNodes = 0;
        for (int v = 0; v < geom.Mesh.NodeCount; v++)
        {
            double x = geom.Mesh.Position(v).X;
            if (x < aRight + margin)
                continue;
            worst = Math.Max(worst, Math.Abs(results.TemperatureAt(v) - Exact(x)));
            maxRise = Math.Max(maxRise, Exact(x) - T0);
            checkedNodes++;
        }

        double resistance = L / (k * area);                  // R = L/(kA), K/mW
        double riseByFormula = power * PcbThermalModel.MilliwattsPerWatt * resistance;
        output.WriteLine($"{geom.Mesh.ElementCount:N0} elements, k_in {k:g4} mW/(mm.K)");
        output.WriteLine($"  R = L/(kA) = {resistance:g4} K/mW, so P*R = {riseByFormula:g6} K "
            + $"(edge->far, if the source were at x=0)");
        output.WriteLine($"  applied {Q / PcbThermalModel.MilliwattsPerWatt:g6} W over the footprint");
        output.WriteLine($"  far-field ({checkedNodes} nodes) worst |T - linear| {worst:E3} K "
            + $"on a {maxRise:g4} K rise -> {worst / maxRise:E3} relative");
        output.WriteLine($"  energy balance: applied {results.Report.AppliedHeat:g6}, prescribed "
            + $"{results.Report.PrescribedHeat:g6} mW, residual {results.Report.EnergyBalanceResidual:E3}");

        Assert.True(checkedNodes > 0, "no far-field nodes to check");
        // The exact statement: all the generated heat leaves the cold edge (energy conservation).
        Assert.True(results.Report.EnergyBalanceResidual < 1e-10);
        Assert.Equal(-results.Report.AppliedHeat, results.Report.PrescribedHeat, 6);
        // The stated accuracy: the far-field is the 1D series-resistance line to a few parts in 1e5.
        Assert.True(worst / maxRise < 1e-3, $"series-resistance relative error {worst / maxRise:E3}");
        // The hot-spot temperature is under the component and is the board peak here.
        Assert.Equal(results.MaxTemperature, geom.FootprintPeak(results, reference), 9);
    }

    // ---- 3. copper raises spreading (lowers the peak) ----

    /// <summary>
    /// <b>Copper raises spreading.</b> The SAME power and footprint with a realistic copper
    /// fraction lifts the effective in-plane conductivity, so the board conducts the heat away
    /// better and the peak drops. In the far-field 1D region the rise is exactly inverse in the
    /// in-plane conductivity, so <c>rise_copper / rise_bare = k_bare / k_copper</c> — a ratio, not
    /// a hand-waved direction. Real FR4 here, so the improvement is the realistic order of
    /// magnitude a ground plane buys.
    /// </summary>
    [Fact]
    public void Copper_RaisesSpreadingAndLowersThePeak()
    {
        const double L = 40, d = 20, t = 1.6, a = 8, T0 = 25, power = 0.3;
        double coverage = 2 * 0.035 * 0.6;   // two 35 um layers, 60% average coverage (mm of copper)

        PcbThermalSpec Spec(double fraction) => new()
        {
            // Real FR4 (k = 0.3), so the copper effect is the real ~20x, not a clean-number 5x.
            CopperFraction = fraction,
            MaxElementSize = 4,
            ComponentPower = new Dictionary<string, double> { ["U1"] = power },
            Boundaries =
            [
                PcbThermalBoundary.FixedTemperature(
                    "cold edge", Facets.OnPlane(new Vector3d(L, 0, 0), Vector3d.UnitX), T0),
            ],
        };

        double fCopper = coverage / t;                       // copper volume fraction
        var bare = PcbThermalModel.Build(HeaterLayout(L, d, t, a, out _), Spec(0));
        var copper = PcbThermalModel.Build(HeaterLayout(L, d, t, a, out _), Spec(fCopper));
        var bareR = ThermalSolver.Solve(bare.Model);
        var copperR = ThermalSolver.Solve(copper.Model);

        double bareRise = bareR.MaxTemperature - T0;
        double copperRise = copperR.MaxTemperature - T0;
        output.WriteLine($"copper fraction {fCopper:g4}: k_in {bare.ConductivityInPlane:g4} -> "
            + $"{copper.ConductivityInPlane:g4} mW/(mm.K)");
        output.WriteLine($"  peak rise {bareRise:g6} K -> {copperRise:g6} K "
            + $"({bareRise / copperRise:g4}x lower)");

        // Direction: copper spreads the heat, so the peak drops (and substantially).
        Assert.True(copperRise < bareRise, "copper should lower the peak");
        Assert.True(bareRise / copperRise > 5, $"expected a large drop, got {bareRise / copperRise:g4}x");

        // The RATIO: at a shared far-field node the rise is inverse in k_in, so the peak rise ratio
        // is k_copper / k_bare (the source geometry, Q and A are identical between the two runs).
        double node = 30;   // a far-field x, past the source
        double bareFar = bareR.TemperatureAt(NodeNearestX(bare.Mesh, node)) - T0;
        double copperFar = copperR.TemperatureAt(NodeNearestX(copper.Mesh, node)) - T0;
        double ratio = bareFar / copperFar;
        double expected = copper.ConductivityInPlane / bare.ConductivityInPlane;
        output.WriteLine($"  far-field rise ratio {ratio:g6} vs k_copper/k_bare {expected:g6}");
        Assert.True(Math.Abs(ratio - expected) / expected < 0.02,
            $"rise ratio {ratio:g6} vs expected {expected:g6}");
    }

    // ---- 4. a board with no boundary condition is refused by name ----

    /// <summary>An undriven conduction problem — power applied but nothing setting the temperature
    /// level — is refused BY NAME (the <see cref="ThermalSolver"/> convention), not solved to an
    /// arbitrary offset.</summary>
    [Fact]
    public void NoBoundaryCondition_IsRefusedByName()
    {
        var spec = new PcbThermalSpec { Dielectric = CleanDielectric, BoardPower = 1.0 };
        var ex = Assert.Throws<FeaException>(() => PcbThermal.Solve(BareLayout(40, 20, 1.5), spec));
        output.WriteLine(ex.Message);
        Assert.Contains("no prescribed temperature", ex.Message);
        Assert.Contains("convective", ex.Message);
    }

    // ---- 5. a zero-power board is isothermal at the edge temperature ----

    /// <summary>A board with no power and one held edge is uniform at the held temperature,
    /// EXACTLY — a constant field is reproduced to round-off (the patch test), and there is no
    /// source to disturb it.</summary>
    [Fact]
    public void ZeroPower_IsIsothermalAtTheEdge()
    {
        const double T0 = 40;
        var spec = new PcbThermalSpec
        {
            Dielectric = CleanDielectric,
            Boundaries = [PcbThermalBoundary.FixedTemperature(BoardSurface.Edges, T0)],
        };
        var result = PcbThermal.Solve(BareLayout(40, 20, 1.5), spec);
        output.WriteLine($"temperature {result.MinTemperature:g10} to {result.PeakTemperature:g10}");
        Assert.Equal(T0, result.PeakTemperature, 10);
        Assert.Equal(T0, result.MinTemperature, 10);
    }

    // ---- 6. determinism ----

    /// <summary>The same layout and spec solve bit-for-bit identically — Build is a pure function
    /// and the solver is deterministic.</summary>
    [Fact]
    public void Solve_IsDeterministic()
    {
        const double L = 40, d = 20, t = 1.5;
        var spec = new PcbThermalSpec
        {
            Dielectric = CleanDielectric,
            BoardPower = 0.5,
            MaxElementSize = 6,
            Boundaries =
            [
                PcbThermalBoundary.FixedTemperature(
                    "cold", Facets.OnPlane(new Vector3d(L, 0, 0), Vector3d.UnitX), 20),
            ],
        };
        var a = PcbThermal.Solve(BareLayout(L, d, t), spec).Fea;
        var b = PcbThermal.Solve(BareLayout(L, d, t), spec).Fea;
        Assert.Equal(a.Temperature.Count, b.Temperature.Count);
        int differing = 0;
        for (int v = 0; v < a.Temperature.Count; v++)
            if (BitConverter.DoubleToInt64Bits(a.TemperatureAt(v))
                != BitConverter.DoubleToInt64Bits(b.TemperatureAt(v)))
                differing++;
        output.WriteLine($"{differing} of {a.Temperature.Count} nodal temperatures differ");
        Assert.Equal(0, differing);
    }

    // ---- 7. convection: the film-coefficient unit and the global balance ----

    /// <summary>
    /// <b>Convection wired and scaled right.</b> A high-conductivity (copper-heavy) board is nearly
    /// isothermal, so the whole of a stated power leaves by convection and the board settles near
    /// <c>T∞ + P/(h·A)</c>. Two things ride on it: the energy balance (convected = generated) is an
    /// identity a correct solve satisfies to round-off, and the RISE confirms the film-coefficient
    /// conversion (SI W/(m²·K) → the model unit) — forget the ×1e-3 and the rise is 1000× off.
    /// </summary>
    [Fact]
    public void Convection_CoolsToAmbientAtTheRightMagnitude()
    {
        const double L = 30, d = 30, t = 1.6, ambient = 25, power = 0.5, hSi = 20;   // W/(m^2.K)
        // Copper-heavy so the board is nearly isothermal and the lumped balance is a good oracle.
        var spec = new PcbThermalSpec
        {
            CopperFraction = 0.9,
            MaxElementSize = 5,
            BoardPower = power,
            Boundaries =
            [
                PcbThermalBoundary.Convection(BoardSurface.Top, hSi, ambient),
                PcbThermalBoundary.Convection(BoardSurface.Bottom, hSi, ambient),
            ],
        };
        var result = PcbThermal.Solve(BareLayout(L, d, t), spec);

        // Lumped estimate: P leaves through top + bottom (area 2*L*d) at h*(T - Tinf).
        double hModel = hSi * PcbThermalModel.ModelFilmPerSi;   // mW/(mm^2.K)
        double convArea = 2 * L * d;
        double lumpedRise = power * PcbThermalModel.MilliwattsPerWatt / (hModel * convArea);
        double meanRise = 0.5 * (result.PeakTemperature + result.MinTemperature) - ambient;

        output.WriteLine($"h = {hSi} W/(m^2.K) = {hModel:g4} mW/(mm^2.K)");
        output.WriteLine($"  lumped rise P/(hA) = {lumpedRise:g4} K, mean board rise {meanRise:g4} K");
        output.WriteLine($"  balance: applied {result.Report.AppliedHeat:g6}, "
            + $"convective {result.Report.ConvectiveHeat:g6} mW, "
            + $"residual {result.Report.EnergyBalanceResidual:E3}");

        // The energy balance is an identity — convected must equal generated to round-off.
        Assert.True(result.Report.EnergyBalanceResidual < 1e-9);
        Assert.Equal(result.Report.AppliedHeat, result.Report.ConvectiveHeat, 6);
        // The rise confirms the film conversion: a near-isothermal board lands within a few % of
        // the lumped P/(hA) (the in-plane gradient is the only departure).
        Assert.True(Math.Abs(meanRise - lumpedRise) / lumpedRise < 0.05,
            $"mean rise {meanRise:g4} vs lumped {lumpedRise:g4}");
    }

    // ---- 8. the effective-conductivity mixing rule ----

    /// <summary>The mixing rule pinned directly: in-plane is the parallel rule of mixtures,
    /// through-thickness the series harmonic mean, and a bare board collapses both to the
    /// dielectric's own conductivity (the isotropic path).</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    [InlineData(0.5)]
    public void MixingRule_IsParallelInPlaneAndSeriesThrough(double f)
    {
        double kCu = PcbMaterials.Copper.ThermalConductivity;
        double kFr4 = PcbMaterials.Fr4.ThermalConductivity;
        var geom = PcbThermalModel.Build(BareLayout(20, 20, 1.6),
            new PcbThermalSpec { CopperFraction = f, MaxElementSize = 10 });

        double inPlane = f * kCu + (1 - f) * kFr4;
        double through = 1.0 / (f / kCu + (1 - f) / kFr4);
        Assert.Equal(inPlane, geom.ConductivityInPlane, 10);
        Assert.Equal(through, geom.ConductivityThrough, 10);
        Assert.True(geom.ConductivityInPlane >= geom.ConductivityThrough);   // parallel >= series
        if (f == 0)
            Assert.Equal(kFr4, geom.ConductivityThrough, 12);   // bare = isotropic dielectric
    }

    // ---- 9. an end-to-end realistic board ----

    /// <summary>A small board with two powered components and a cold edge: the field is produced,
    /// the hotter component reads hotter, and the peak is under the components rather than at the
    /// cold edge.</summary>
    [Fact]
    public void TwoComponents_ProduceSaneHotSpots()
    {
        var board = new PcbBoard(
            [new Vector2d(0, 0), new Vector2d(40, 0), new Vector2d(40, 30), new Vector2d(0, 30)],
            thickness: 1.6);
        var mcu = new PartDefinition("MCU", "U", [new Pin("1", PinType.Passive)],
            new Footprint("qfp", [new Pad("1", new Vector2d(0, 0), 7, 7, PadShape.Rectangular)]));
        var reg = new PartDefinition("REG", "U", [new Pin("1", PinType.Passive)],
            new Footprint("sot", [new Pad("1", new Vector2d(0, 0), 4, 4, PadShape.Rectangular)]));
        var sch = new Schematic("gadget");
        sch.Add("U1", mcu);
        sch.Add("U2", reg);
        var layout = new PcbLayout(sch, board);
        layout.Place("U1", 12, 15);
        layout.Place("U2", 28, 15);

        var spec = new PcbThermalSpec
        {
            CopperFraction = 0.03,
            MaxElementSize = 4,
            ComponentPower = new Dictionary<string, double> { ["U1"] = 0.4, ["U2"] = 1.0 },
            Boundaries =
            [
                PcbThermalBoundary.FixedTemperature(
                    "chassis", Facets.OnPlane(new Vector3d(0, 0, 0), Vector3d.UnitX), 40),
            ],
        };
        var result = PcbThermal.Solve(layout, spec);

        output.WriteLine(result.ToString());
        foreach (var (r, temp) in result.ComponentTemperature)
            output.WriteLine($"  {r}: {temp:g6} C");

        // The regulator dissipates 2.5x the MCU and is further from the cold edge, so it is hotter.
        Assert.True(result.TemperatureOf("U2") > result.TemperatureOf("U1"));
        // Both hot-spots are above the held edge, and the peak is under a component.
        Assert.True(result.TemperatureOf("U1") > 40 && result.TemperatureOf("U2") > 40);
        Assert.Equal(result.PeakTemperature, result.TemperatureOf("U2"), 6);
        Assert.True(result.MinTemperature <= 40 + 1e-6);   // the cold edge is the coolest
        // The field is produced (two fields: temperature + heat flux).
        Assert.Equal(2, result.Fields().Count);
    }

    private static int NodeNearestX(AnalysisMesh mesh, double x)
    {
        int best = 0;
        double bestDist = double.PositiveInfinity;
        for (int v = 0; v < mesh.NodeCount; v++)
        {
            double dx = Math.Abs(mesh.Position(v).X - x);
            if (dx < bestDist)
            {
                bestDist = dx;
                best = v;
            }
        }
        return best;
    }
}
