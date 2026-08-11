using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;

namespace EngrCAD.Ecad;

/// <summary>
/// Per-component power dissipation solved as heat conduction through the board — the ECAD/thermal
/// coupling. <see cref="Solve"/> meshes the board, assigns the effective (copper-smeared)
/// conductivity, applies the power and boundary conditions from a <see cref="PcbThermalSpec"/>,
/// runs the landed <see cref="ThermalSolver"/>, and returns the temperature field plus the
/// per-component hot-spot temperatures and the board peak.
///
/// <para><b>It stands on the FEA thermal solver rather than a toy</b> precisely so its answers are
/// verifiable against closed forms: a uniformly-dissipating board to a fixed edge matches the
/// analytic conduction parabola (exact for quadratic elements), and a single hot component to a
/// cold edge matches a series-resistance rise. Copper raises the effective in-plane conductivity
/// and so spreads the heat and lowers the peak, measurably.</para>
/// </summary>
public static class PcbThermal
{
    /// <summary>
    /// Solves the board's steady temperature field for a placed layout and a thermal spec.
    /// </summary>
    /// <param name="layout">The placed board layout (its board, placements and footprints).</param>
    /// <param name="spec">Materials, copper fraction, power and boundary conditions.</param>
    /// <returns>The temperature field, the per-component hot-spot temperatures, and the peak.</returns>
    /// <exception cref="FeaException">The board has no boundary condition setting its temperature
    /// level — an undriven conduction problem, refused by name (the <see cref="ThermalSolver"/>
    /// convention: prescribe a temperature somewhere, or give a surface convection).</exception>
    public static PcbThermalResult Solve(PcbLayout layout, PcbThermalSpec? spec = null)
    {
        var geom = PcbThermalModel.Build(layout, spec);
        var results = ThermalSolver.Solve(geom.Model);
        return new PcbThermalResult(geom, results);
    }
}

/// <summary>
/// The answer to a board thermal solve: the underlying <see cref="ThermalResults"/> (its flux,
/// fields, <c>.vtu</c> export), the board's peak and minimum temperature, and the per-component
/// hot-spot temperatures.
/// </summary>
public sealed class PcbThermalResult
{
    private readonly PcbThermalModel _geom;
    private readonly Dictionary<string, double> _componentTemperature;

    internal PcbThermalResult(PcbThermalModel geom, ThermalResults fea)
    {
        _geom = geom;
        Fea = fea;
        _componentTemperature = ComputeComponentTemperatures(geom, fea);
    }

    /// <summary>The underlying FEA conduction result — heat flux, field publishing, <c>.vtu</c>
    /// export, the solve report.</summary>
    public ThermalResults Fea { get; }

    /// <summary>The solve report (sizes, residual, energy balance, applied heat).</summary>
    public ThermalSolveReport Report => Fea.Report;

    /// <summary>The board's peak (hottest) nodal temperature.</summary>
    public double PeakTemperature => Fea.MaxTemperature;

    /// <summary>The board's minimum (coolest) nodal temperature.</summary>
    public double MinTemperature => Fea.MinTemperature;

    /// <summary>The position of the hottest node (board-local coordinates).</summary>
    public Vector3d PeakLocation => _geom.Mesh.Position(Fea.MaxTemperatureNode);

    /// <summary>The effective in-plane conductivity used (mW/(mm·K), the SI W/(m·K)).</summary>
    public double ConductivityInPlane => _geom.ConductivityInPlane;

    /// <summary>The effective through-thickness conductivity used (mW/(mm·K)).</summary>
    public double ConductivityThrough => _geom.ConductivityThrough;

    /// <summary>Per-component hot-spot temperature (reference designator → the peak nodal
    /// temperature under its footprint) — a component's junction/case estimate. Only components
    /// with a placed footprint appear.</summary>
    public IReadOnlyDictionary<string, double> ComponentTemperature => _componentTemperature;

    /// <summary>The hot-spot temperature of one component (its footprint's peak), or NaN when it
    /// has no placed footprint.</summary>
    public double TemperatureOf(string reference) =>
        _componentTemperature.TryGetValue(reference, out double t) ? t : double.NaN;

    /// <summary>The temperature and heat-flux fields over the analysis mesh nodes — the form for
    /// a <c>.vtu</c> export. To colour a <c>Part</c>, use <see cref="SampleOnto"/>.</summary>
    public IReadOnlyList<MeshField> Fields() => Fea.Fields();

    /// <summary>The results resampled onto a display mesh (the board part's mesh) for a
    /// <c>FieldDisplay</c> — exact where the two meshes share a vertex.</summary>
    public IReadOnlyList<MeshField> SampleOnto(HalfEdgeMesh displayMesh) => Fea.SampleOnto(displayMesh);

    private static Dictionary<string, double> ComputeComponentTemperatures(
        PcbThermalModel geom, ThermalResults fea)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var reference in geom.FootprintBoxes.Keys)
            result[reference] = geom.FootprintPeak(fea, reference);
        return result;
    }

    /// <summary>A one-line summary of the board thermal result.</summary>
    public override string ToString() =>
        $"board temperature {MinTemperature:g6} to {PeakTemperature:g6}, "
        + $"k_in = {ConductivityInPlane:g4}, k_th = {ConductivityThrough:g4}, "
        + $"applied {Report.AppliedHeat / PcbThermalModel.MilliwattsPerWatt:g4} W";
}
