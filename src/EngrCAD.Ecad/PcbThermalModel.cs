using EngrCAD.Core;
using EngrCAD.Fea;
using EngrCAD.Mesh;
using EngrCAD.Modeling;

namespace EngrCAD.Ecad;

/// <summary>
/// The two board materials the thermal coupling needs, in the same mm/N/tonne/s unit system
/// <see cref="ModelUnits"/> states once for the whole repository: conductivity in mW/(mm·K)
/// — numerically the SI W/(m·K) — density in tonne/mm³ and specific heat in mm²/(s²·K) (the SI
/// J/(kg·K) times 1e6). Only conductivity is used by a steady solve; density and specific heat
/// are carried so a future transient board warm-up is honest, and so the effective slab has a
/// real heat capacity.
///
/// <para><b>Nominal values, ⚠ verify against the laminate datasheet.</b> A board's dielectric
/// is not one material — FR4's through-thickness conductivity (~0.3) is well below its in-plane
/// value (the glass weave conducts sideways) — but that anisotropy is small next to the copper's,
/// so v1 states the DIELECTRIC isotropic (the through-thickness figure) and lets the copper
/// fraction carry the in-plane spreading. Copper's own figure varies with alloy/plating; 385 is
/// the textbook value for the electrodeposited copper a board is plated with.</para>
/// </summary>
public static class PcbMaterials
{
    /// <summary>FR4 glass-epoxy laminate: k ≈ 0.3 W/(m·K) (through-thickness), rho ≈ 1850 kg/m³,
    /// c ≈ 1100 J/(kg·K). The dielectric the board is extruded from.</summary>
    public static Material Fr4 { get; } =
        new("FR4", density: 1.85e-9, thermalConductivity: 0.3, specificHeat: 1.10e9);

    /// <summary>Copper (electrodeposited): k ≈ 385 W/(m·K), rho ≈ 8960 kg/m³, c ≈ 385 J/(kg·K).
    /// The layers that raise a board's in-plane conductivity by a factor of a thousand.</summary>
    public static Material Copper { get; } =
        new("Copper", density: 8.96e-9, thermalConductivity: 385.0, specificHeat: 3.85e8);
}

/// <summary>Which face(s) of a board a thermal boundary condition acts on — the named surfaces of
/// the board slab, resolved to facet selectors against the meshed slab (z ∈ [0, thickness] in the
/// board's own coordinates).</summary>
public enum BoardSurface
{
    /// <summary>The top face (z = thickness).</summary>
    Top,

    /// <summary>The bottom face (z = 0).</summary>
    Bottom,

    /// <summary>The perimeter side walls (the extruded edges — normal ⊥ z). A board edge clamped
    /// to a heatsink, or exchanging heat with a chassis rail.</summary>
    Edges,

    /// <summary>Every boundary face.</summary>
    All,
}

/// <summary>
/// A thermal boundary condition on a board's surface — a HELD temperature (a heatsink/enclosure
/// wall, a cold edge) or CONVECTION to ambient (a film coefficient over a face). Either a named
/// <see cref="BoardSurface"/> or a raw facet selector (the escape hatch for one edge of a
/// rectangular board, which the four-wall <see cref="BoardSurface.Edges"/> cannot pick out).
///
/// <para><b>The one place a domain unit crosses into the model unit is CONVECTION.</b> A film
/// coefficient is stated in the SI W/(m²·K) an engineer looks up (natural convection in still air
/// is ~10), and converted once, here, to the mW/(mm²·K) the solver consumes — SI × 1e-3. A held
/// temperature and an ambient carry no length or mass, so they cross verbatim (whatever scale the
/// caller works in, °C or K).</para>
/// </summary>
public sealed class PcbThermalBoundary
{
    private enum Kind { FixedTemperature, Convection }

    private readonly Kind _kind;
    private readonly BoardSurface? _surface;
    private readonly Func<FacetRef, bool>? _selector;
    private readonly double _a;    // temperature, or SI film coefficient
    private readonly double _b;    // ambient (convection only)

    private PcbThermalBoundary(
        Kind kind, BoardSurface? surface, Func<FacetRef, bool>? selector,
        string name, double a, double b)
    {
        _kind = kind;
        _surface = surface;
        _selector = selector;
        Name = name;
        _a = a;
        _b = b;
    }

    /// <summary>A name for reports (the surface it acts on, or a caller-given label).</summary>
    public string Name { get; }

    /// <summary>Holds a board <paramref name="surface"/> at a fixed <paramref name="temperature"/>
    /// (a heatsink face, a clamped edge). The temperature is in the caller's own scale.</summary>
    public static PcbThermalBoundary FixedTemperature(BoardSurface surface, double temperature) =>
        new(Kind.FixedTemperature, surface, null, $"{surface} held at {temperature:g6}",
            temperature, 0);

    /// <summary>Holds the facets a selector picks at a fixed temperature — the escape hatch for a
    /// single named edge (one wall of a rectangular board), which <see cref="BoardSurface.Edges"/>
    /// cannot single out.</summary>
    public static PcbThermalBoundary FixedTemperature(
        string name, Func<FacetRef, bool> facets, double temperature)
    {
        ArgumentNullException.ThrowIfNull(facets);
        return new(Kind.FixedTemperature, null, facets, name ?? "", temperature, 0);
    }

    /// <summary>Convection over a board <paramref name="surface"/>: <c>q = h·(T − T∞)</c> out of the
    /// board. <paramref name="filmCoefficientSi"/> is in W/(m²·K) (natural air ~10, a forced fan
    /// ~50–100) and is converted once to the model unit; <paramref name="ambient"/> is the air
    /// temperature in the caller's scale.</summary>
    public static PcbThermalBoundary Convection(
        BoardSurface surface, double filmCoefficientSi, double ambient) =>
        new(Kind.Convection, surface, null,
            $"convection h = {filmCoefficientSi:g6} W/(m^2.K) to {ambient:g6} on {surface}",
            filmCoefficientSi, ambient);

    /// <summary>Convection over the facets a selector picks (the escape hatch).</summary>
    public static PcbThermalBoundary Convection(
        string name, Func<FacetRef, bool> facets, double filmCoefficientSi, double ambient)
    {
        ArgumentNullException.ThrowIfNull(facets);
        return new(Kind.Convection, null, facets, name ?? "", filmCoefficientSi, ambient);
    }

    /// <summary>Applies this condition to a built thermal model.</summary>
    internal void Apply(PcbThermalModel geom)
    {
        var facets = _selector ?? geom.Surface(_surface!.Value);
        if (_kind == Kind.FixedTemperature)
            geom.Model.Temperature(facets, _a);
        else
            // SI W/(m^2.K) -> model mW/(mm^2.K): the one boundary unit conversion (see the type doc).
            geom.Model.Convection(facets, _a * PcbThermalModel.ModelFilmPerSi, _b);
    }
}

/// <summary>
/// How a board is turned into a thermal problem: the materials and copper fraction the effective
/// conductivity is mixed from, the element order and mesh size, the per-component (and diffuse)
/// power, and the boundary conditions.
///
/// <para><b>Power is stated in WATTS</b> — the domain's universal unit; nobody specs a chip in
/// milliwatts-of-model-units. It is converted once to the model's mW inside
/// <see cref="PcbThermalModel"/>, so a report can print watts while the equation consumes the
/// model unit (the <c>ModelUnits</c> discipline: the input a caller states is converted at the
/// boundary; the field the solver integrates is native).</para>
/// </summary>
public sealed record PcbThermalSpec
{
    /// <summary>The board's dielectric (default <see cref="PcbMaterials.Fr4"/>).</summary>
    public Material Dielectric { get; init; } = PcbMaterials.Fr4;

    /// <summary>The copper layers' material (default <see cref="PcbMaterials.Copper"/>).</summary>
    public Material Copper { get; init; } = PcbMaterials.Copper;

    /// <summary>
    /// The copper VOLUME fraction of the board, in [0, 1] — the one honest knob of the smeared
    /// model. It is (total copper layer thickness × average coverage) / board thickness: a
    /// two-layer 1.6 mm board with 35 µm copper at 50 % average coverage is
    /// <c>2 × 0.035 × 0.5 / 1.6 ≈ 0.022</c>. Default 0 = bare dielectric (an isotropic
    /// conductivity, the verification simplification). <see cref="FromCoverage"/> derives it from
    /// a board's own stackup.
    /// </summary>
    public double CopperFraction { get; init; }

    /// <summary>Element order (default quadratic — a board's temperature field is smooth and
    /// worth the accuracy; the parabolic dissipation profile is then exact).</summary>
    public ElementOrder Order { get; init; } = ElementOrder.Quadratic;

    /// <summary>Maximum tet element size (mm), or null for a size derived from the board's own
    /// dimensions. A coarse mesh is fine for a smeared homogeneous slab.</summary>
    public double? MaxElementSize { get; init; }

    /// <summary>Mesh quality for the display-mesh lowering the board slab is built from (null =
    /// the default).</summary>
    public MeshQuality? Quality { get; init; }

    /// <summary>A uniform volumetric dissipation over the WHOLE board, in watts — the diffuse
    /// case (a resistive heater plane, or the aggregate of many tiny parts). Spread exactly, so
    /// its resultant is the stated power to round-off.</summary>
    public double BoardPower { get; init; }

    /// <summary>Per-component dissipation (reference designator → watts), spread uniformly over
    /// each component's footprint × board thickness. A component named here must be placed and
    /// carry a footprint.</summary>
    public IReadOnlyDictionary<string, double> ComponentPower { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>The boundary conditions (held temperatures, convection). A board with NONE and any
    /// applied power is an undriven conduction problem and is refused by name at the solve (the
    /// <see cref="ThermalSolver"/> convention).</summary>
    public IReadOnlyList<PcbThermalBoundary> Boundaries { get; init; } = [];

    /// <summary>
    /// Derives the copper fraction from a board's own <see cref="LayerStackup"/> (its copper
    /// layers' thicknesses summed and averaged over the board thickness) times a stated average
    /// <paramref name="coverage"/> — the fraction of each copper layer that is actually copper (a
    /// signal layer might be 30–50 %, a plane 80–95 %). A board with no physical stackup keeps the
    /// fraction as the caller's own <paramref name="coverage"/> applied to a single copper-layer
    /// thickness of 35 µm per copper plane.
    /// </summary>
    public PcbThermalSpec FromCoverage(PcbBoard board, double coverage)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (!(coverage >= 0 && coverage <= 1))
            throw new ArgumentOutOfRangeException(nameof(coverage), coverage,
                "Copper coverage is a fraction in [0, 1].");
        double copperThickness = board.LayerStackup is { } stack
            ? stack.Layers.Where(l => l.Kind == StackLayerKind.Copper).Sum(l => l.Thickness)
            : board.Stackup.Coppers.Count * 0.035;
        return this with { CopperFraction = copperThickness * coverage / board.Thickness };
    }
}

/// <summary>
/// A board turned into an FEA heat-conduction problem — the bridge between the ECAD board model and
/// the landed thermal solver. It meshes the board as a homogeneous slab, assigns the effective
/// conductivity, applies the power as a <see cref="ThermalModel.Generation(double)"/> load and the
/// boundary conditions, and exposes the built <see cref="Model"/> so a caller can add its own
/// conditions with the same <see cref="Facets"/> vocabulary before solving.
///
/// <para><b>v1 is the standard board-level model: an effective anisotropic conductivity over a
/// slab.</b> The copper is not modelled as discrete traces/planes; it is SMEARED into the slab's
/// conductivity, high in-plane (the copper layers are PARALLEL heat paths, so an area-fraction
/// rule of mixtures <c>k_in = f·k_Cu + (1−f)·k_FR4</c>) and low through-thickness (the layers are
/// in SERIES, so the harmonic mean <c>k_th = 1 / (f/k_Cu + (1−f)/k_FR4)</c>). This is the model a
/// conforming multi-body copper mesh would refine, and it needs no such mesh — the reason it is
/// v1. The board is a SLAB with no holes (the smear ignores the copper geometry, so it ignores the
/// drills too); the closed-form slab and series-resistance oracles are then clean.</para>
///
/// <para>A bare board (<see cref="PcbThermalSpec.CopperFraction"/> = 0) reduces to an ISOTROPIC
/// FR4 conductivity — the two effective values collapse to <c>k_FR4</c> — and the model takes the
/// scalar conductivity path bit-for-bit, which is the verification simplification.</para>
/// </summary>
public sealed class PcbThermalModel
{
    /// <summary>Model power unit conversion: 1 W = 1e3 mW (the mm/N/tonne/s power unit).</summary>
    internal const double MilliwattsPerWatt = 1000.0;

    /// <summary>Film-coefficient conversion: model mW/(mm²·K) = SI W/(m²·K) × 1e-3.</summary>
    internal const double ModelFilmPerSi = 1e-3;

    private readonly Dictionary<string, Aabb> _footprintBoxes;

    private PcbThermalModel(
        ThermalModel model, HalfEdgeMesh slab, double thickness,
        double inPlane, double through, Dictionary<string, Aabb> footprintBoxes)
    {
        Model = model;
        SlabMesh = slab;
        Thickness = thickness;
        ConductivityInPlane = inPlane;
        ConductivityThrough = through;
        _footprintBoxes = footprintBoxes;
    }

    /// <summary>The built FEA thermal model — conductivity, generation and any boundary conditions
    /// already applied. A caller may add more conditions (with the <see cref="Facets"/> vocabulary)
    /// before calling <see cref="ThermalSolver.Solve"/>, or use <see cref="PcbThermal.Solve"/>.</summary>
    public ThermalModel Model { get; }

    /// <summary>The analysis mesh.</summary>
    public AnalysisMesh Mesh => Model.Mesh;

    /// <summary>The board slab's surface mesh (board-local coordinates, z ∈ [0, thickness]) — the
    /// mesh the analysis boundary nodes live on, and a surface a result can sample onto.</summary>
    public HalfEdgeMesh SlabMesh { get; }

    /// <summary>The board thickness (mm).</summary>
    public double Thickness { get; }

    /// <summary>The effective in-plane conductivity (mW/(mm·K), the SI W/(m·K)).</summary>
    public double ConductivityInPlane { get; }

    /// <summary>The effective through-thickness conductivity (mW/(mm·K)).</summary>
    public double ConductivityThrough { get; }

    /// <summary>The board-local footprint boxes of the placed components (xy from the pads, z
    /// spanning the board thickness) — the regions component power is spread over and component
    /// temperature is read from.</summary>
    public IReadOnlyDictionary<string, Aabb> FootprintBoxes => _footprintBoxes;

    /// <summary>The peak nodal temperature under a component's footprint (xy in its footprint box,
    /// any z) — its hot-spot / junction estimate — or NaN when it has no placed footprint.</summary>
    public double FootprintPeak(ThermalResults results, string reference)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (!_footprintBoxes.TryGetValue(reference, out var box))
            return double.NaN;
        double peak = double.NegativeInfinity;
        for (int v = 0; v < Mesh.NodeCount; v++)
        {
            var p = Mesh.Position(v);
            if (p.X >= box.Min.X && p.X <= box.Max.X && p.Y >= box.Min.Y && p.Y <= box.Max.Y)
                peak = Math.Max(peak, results.TemperatureAt(v));
        }
        return double.IsNegativeInfinity(peak) ? double.NaN : peak;
    }

    /// <summary>Resolves a named board surface to a facet selector against the meshed slab.</summary>
    public Func<FacetRef, bool> Surface(BoardSurface surface) => surface switch
    {
        BoardSurface.Top => Facets.OnPlane(new Vector3d(0, 0, Thickness), Vector3d.UnitZ),
        BoardSurface.Bottom => Facets.OnPlane(Vector3d.Zero, Vector3d.UnitZ),
        // The perimeter: a side-wall facet's outward normal is ⊥ z. Dimensionless, so an absolute
        // constant is correct (the top/bottom facets read |n.z| = 1, orders of magnitude away).
        BoardSurface.Edges => f => Math.Abs(f.Normal.Z) <= 1e-3,
        _ => Facets.All,
    };

    /// <summary>
    /// Builds the thermal model for a placed layout: meshes the board slab, mixes and assigns the
    /// effective conductivity, applies the board and per-component power, and applies the boundary
    /// conditions from the spec.
    /// </summary>
    public static PcbThermalModel Build(PcbLayout layout, PcbThermalSpec? spec = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        spec ??= new PcbThermalSpec();
        var board = layout.Board;

        if (!(spec.CopperFraction >= 0 && spec.CopperFraction <= 1))
            throw new ArgumentOutOfRangeException(nameof(spec), spec.CopperFraction,
                "PcbThermalSpec.CopperFraction is a volume fraction in [0, 1].");
        if (spec.Dielectric.ThermalConductivity <= 0)
            throw new ArgumentException(
                $"The dielectric '{spec.Dielectric.Name}' states no thermal conductivity; a "
                + "conduction solve needs a positive one (see PcbMaterials.Fr4).", nameof(spec));

        // --- effective conductivity (the mixing rule) ---
        double f = spec.CopperFraction;
        double kFr4 = spec.Dielectric.ThermalConductivity;
        double kCu = spec.Copper.ThermalConductivity;
        double inPlane = f * kCu + (1 - f) * kFr4;                    // parallel (rule of mixtures)
        double through = 1.0 / (f / Math.Max(kCu, double.Epsilon) + (1 - f) / kFr4);  // series (harmonic)

        var effective = EffectiveMaterial(spec, f, inPlane);

        // --- the board slab (no holes: the smear ignores the copper, so it ignores the drills) ---
        var slab = Shape.Extrude(board.Outline(), board.Thickness).ToMesh(spec.Quality);
        double maxSize = spec.MaxElementSize ?? DefaultElementSize(board);
        var tets = TetMesher.Mesh(slab, new TetMeshOptions
        {
            RefineQuality = true,
            MaxElementSize = maxSize,
        });
        var mesh = spec.Order == ElementOrder.Linear
            ? AnalysisMesh.Of(tets)
            : AnalysisMesh.Quadratic(tets);

        var model = new ThermalModel(mesh, effective);
        // Only override with a directional law when the two effective values actually differ:
        // a bare board (f = 0) keeps the isotropic scalar path bit-for-bit.
        if (Math.Abs(inPlane - through) > 1e-12 * inPlane)
            model.SetConductivity(0, ConductivityLaw.Orthotropic(
                Frame3d.WorldXY, inPlane, inPlane, through, "board (effective)"));

        var footprintBoxes = FootprintBoxes2d(layout);
        ApplyGeneration(model, layout, board, spec, footprintBoxes);

        var geom = new PcbThermalModel(model, slab, board.Thickness, inPlane, through, footprintBoxes);
        foreach (var boundary in spec.Boundaries)
            boundary.Apply(geom);
        return geom;
    }

    // The effective slab material: the in-plane conductivity as the scalar (so a bare board with no
    // directional law conducts at k_FR4 isotropically), and volume-weighted density and heat
    // capacity so a future transient warm-up integrates a real capacity.
    private static Material EffectiveMaterial(PcbThermalSpec spec, double f, double inPlane)
    {
        double rho = f * spec.Copper.Density + (1 - f) * spec.Dielectric.Density;
        double rhoC = f * spec.Copper.VolumetricHeatCapacity
            + (1 - f) * spec.Dielectric.VolumetricHeatCapacity;
        // Specific heat from the weighted volumetric capacity (c = rho.c / rho); zero density is
        // legal (steady solves ignore it) and gives zero specific heat.
        double c = rho > 0 ? rhoC / rho : 0;
        return new Material("board (effective)",
            density: rho, thermalConductivity: inPlane, specificHeat: c);
    }

    // A size that puts a handful of elements across the board's smaller in-plane dimension, but
    // never below the thickness (so the slab gets an element or two through it).
    private static double DefaultElementSize(PcbBoard board)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var p in board.OutlinePoints)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        double smaller = Math.Min(maxX - minX, maxY - minY);
        return Math.Max(board.Thickness, smaller / 6);
    }

    private static void ApplyGeneration(
        ThermalModel model, PcbLayout layout, PcbBoard board, PcbThermalSpec spec,
        Dictionary<string, Aabb> footprintBoxes)
    {
        double boardVolume = board.OutlineArea() * board.Thickness;

        // Diffuse board power: an exact uniform generation (a constant integrates exactly, so the
        // resultant is the stated power to round-off).
        if (spec.BoardPower != 0)
            model.Generation(spec.BoardPower * MilliwattsPerWatt / boardVolume);

        if (spec.ComponentPower.Count == 0)
            return;

        // Per-component power: a step field of q_i = P_i / (footprint area × thickness) over each
        // footprint box. The field's resultant is exact except at footprint boundaries (a step
        // inside an element), and ThermalSolveReport.AppliedHeat reports the actual integral.
        var rates = new List<(double MinX, double MinY, double MaxX, double MaxY, double Rate)>();
        foreach (var (reference, watts) in spec.ComponentPower)
        {
            if (watts == 0)
                continue;
            if (!footprintBoxes.TryGetValue(reference, out var box))
                throw new ArgumentException(
                    $"Component '{reference}' has {watts:g4} W of dissipation but no placed footprint "
                    + "to spread it over — it must be placed and carry a footprint with pads.",
                    nameof(spec));
            double area = (box.Max.X - box.Min.X) * (box.Max.Y - box.Min.Y);
            if (!(area > 0))
                throw new ArgumentException(
                    $"Component '{reference}' has a zero-area footprint; its power cannot be spread.",
                    nameof(spec));
            rates.Add((box.Min.X, box.Min.Y, box.Max.X, box.Max.Y,
                watts * MilliwattsPerWatt / (area * board.Thickness)));
        }
        if (rates.Count == 0)
            return;

        model.Generation(p =>
        {
            double q = 0;
            foreach (var r in rates)
                if (p.X >= r.MinX && p.X <= r.MaxX && p.Y >= r.MinY && p.Y <= r.MaxY)
                    q += r.Rate;
            return q;
        });
    }

    // Every placed component's board-local footprint box (xy from its pads, z spanning the slab).
    private static Dictionary<string, Aabb> FootprintBoxes2d(PcbLayout layout)
    {
        var boxes = new Dictionary<string, Aabb>(StringComparer.Ordinal);
        double t = layout.Board.Thickness;
        foreach (var placement in layout.Placements)
        {
            var footprint = layout.Schematic.Find(placement.Reference)!.Definition.Footprint;
            if (footprint is null || footprint.Pads.Count == 0)
                continue;
            var pose = layout.PlacementPose(placement);
            var box = Aabb.Empty;
            foreach (var pad in footprint.Pads)
            {
                double hw = pad.Width / 2, hh = pad.Height / 2;
                foreach (var (dx, dy) in new[] { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) })
                {
                    var w = pose.ToWorld(new Vector3d(pad.Center.X + dx, pad.Center.Y + dy, 0));
                    box = box.Union(new Aabb(new Vector3d(w.X, w.Y, 0), new Vector3d(w.X, w.Y, t)));
                }
            }
            if (!box.IsEmpty)
                boxes[placement.Reference] = box;
        }
        return boxes;
    }
}
