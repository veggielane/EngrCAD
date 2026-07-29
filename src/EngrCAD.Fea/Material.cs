using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// An isotropic linear-elastic material: Young's modulus, Poisson's ratio and mass
/// density, with the derived Lame parameters every element formulation actually uses —
/// plus the thermal triple (conductivity, specific heat, expansion coefficient) a
/// <see cref="ThermalModel"/> reads.
///
/// <para><b>Units are the caller's, and they must be CONSISTENT.</b> Nothing in this
/// kernel carries a unit, so a material is only meaningful against a length unit chosen
/// for the model. The catalogue in <see cref="Materials"/> is stated in the
/// <b>mm / N / MPa / tonne</b> system, which is what the rest of EngrCAD assumes (STEP
/// export writes millimetres): lengths in mm, forces in N, stresses in MPa = N/mm²,
/// densities in tonne/mm³ (steel 7850 kg/m³ = 7.85e-9 t/mm³) and gravity 9806.65 mm/s²
/// (<see cref="Materials.GravityMillimetres"/>). The SI alternative — m / N / Pa / kg —
/// works identically; what does not work is mixing them, and no check here can catch
/// that, so the unit system is documented rather than enforced.</para>
///
/// <para><b>The thermal units in that system are worth stating, because two of the three
/// surprise people.</b> With mm / N / tonne / s, energy is N·mm = mJ and power is mJ/s =
/// mW, so:
/// <list type="bullet">
/// <item><description><b>Conductivity</b> is mW/(mm·K), which is <b>numerically identical
/// to the SI W/(m·K)</b> — steel is 50 either way. That coincidence is not luck: the
/// milli- in the power cancels the milli- in the length.</description></item>
/// <item><description><b>Specific heat</b> is mJ/(tonne·K) = mm²/(s²·K), which is the SI
/// J/(kg·K) times <b>1e6</b> — steel's 460 becomes 4.6e8. Equivalently, it is the figure
/// Abaqus asks for in its mm/N/tonne/s system.</description></item>
/// <item><description><b>Expansion</b> is 1/K, the one quantity that carries no length or
/// mass and so reads the same in every system.</description></item>
/// </list>
/// The derived ones follow: a convection coefficient is mW/(mm²·K) = SI W/(m²·K) × 1e-3
/// (natural convection in air, ~10, is 0.01 here), a heat flux is mW/mm² = SI W/m² ×
/// 1e-3, and a volumetric generation is mW/mm³ = SI W/m³ × 1e-6. Thermal diffusivity
/// k/(rho·c) then comes out in mm²/s — steel 13.85, which is the SI 1.385e-5 m²/s.</para>
///
/// <para><b>The constitutive law.</b> For an isotropic material the stress-strain
/// relation in Voigt form (σ = D·ε with <i>engineering</i> shear strains
/// γ = 2ε) is
/// <code>
///        | L+2M   L     L    0  0  0 |                E·nu
///   D =  |  L    L+2M   L    0  0  0 |    with   L = ------------------  (Lame's first)
///        |  L     L    L+2M  0  0  0 |               (1+nu)(1-2nu)
///        |  0     0     0    M  0  0 |
///        |  0     0     0    0  M  0 |                     E
///        |  0     0     0    0  0  M |               M = --------        (shear modulus)
///                                                        2(1+nu)
/// </code>
/// Voigt order throughout this project is (xx, yy, zz, xy, yz, zx).</para>
/// </summary>
public sealed record Material
{
    /// <summary>
    /// Builds a material. <paramref name="poissonsRatio"/> must lie in (-1, 0.5): at 0.5
    /// the material is incompressible and Lame's first parameter diverges, which a
    /// displacement-based formulation cannot represent (it locks). The bound is checked
    /// rather than clamped, because a value at or past it is a modelling error and a
    /// silently adjusted one would produce a stiffness nobody asked for.
    /// </summary>
    /// <param name="name">Display name (appears in reports and refusal messages).</param>
    /// <param name="youngsModulus">E, in the model's stress unit (MPa for mm/N).</param>
    /// <param name="poissonsRatio">nu, dimensionless, in (-1, 0.5).</param>
    /// <param name="density">Mass density, for gravity/body loads (t/mm³ for mm/N). Zero
    /// is legal and means body loads contribute nothing.</param>
    /// <param name="thermalConductivity">k, in mW/(mm·K) — numerically the SI W/(m·K).
    /// Zero means "not stated", and a thermal solve refuses such a material by name rather
    /// than assembling a singular matrix.</param>
    /// <param name="specificHeat">c, in mm²/(s²·K) — the SI J/(kg·K) times 1e6. Needed
    /// only by a transient solve; zero means "not stated" and is refused there.</param>
    /// <param name="thermalExpansion">alpha, in 1/K. Zero is legal and means a temperature
    /// change produces no thermal-expansion load.</param>
    public Material(
        string name,
        double youngsModulus,
        double poissonsRatio,
        double density = 0,
        double thermalConductivity = 0,
        double specificHeat = 0,
        double thermalExpansion = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A material needs a non-empty name.", nameof(name));
        if (!(youngsModulus > 0) || double.IsInfinity(youngsModulus))
            throw new ArgumentOutOfRangeException(nameof(youngsModulus), youngsModulus,
                $"Young's modulus must be finite and positive; '{name}' was given {youngsModulus:G6}.");
        if (!(poissonsRatio > -1.0 && poissonsRatio < 0.5))
            throw new ArgumentOutOfRangeException(nameof(poissonsRatio), poissonsRatio,
                $"Poisson's ratio must lie strictly in (-1, 0.5); '{name}' was given {poissonsRatio:G6}. " +
                "At 0.5 the material is incompressible and Lame's first parameter is infinite, " +
                "which a displacement-based element cannot represent.");
        if (!(density >= 0) || double.IsInfinity(density))
            throw new ArgumentOutOfRangeException(nameof(density), density,
                $"Density must be finite and non-negative; '{name}' was given {density:G6}.");
        RequireNonNegative(name, thermalConductivity, nameof(thermalConductivity), "Thermal conductivity");
        RequireNonNegative(name, specificHeat, nameof(specificHeat), "Specific heat");
        RequireNonNegative(name, thermalExpansion, nameof(thermalExpansion), "Thermal expansion");

        Name = name;
        YoungsModulus = youngsModulus;
        PoissonsRatio = poissonsRatio;
        Density = density;
        ThermalConductivity = thermalConductivity;
        SpecificHeat = specificHeat;
        ThermalExpansion = thermalExpansion;

        Mu = youngsModulus / (2.0 * (1.0 + poissonsRatio));
        Lambda = youngsModulus * poissonsRatio / ((1.0 + poissonsRatio) * (1.0 - 2.0 * poissonsRatio));
    }

    private static void RequireNonNegative(string name, double value, string parameter, string label)
    {
        if (!(value >= 0) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(parameter, value,
                $"{label} must be finite and non-negative; '{name}' was given {value:G6}.");
    }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Young's modulus E.</summary>
    public double YoungsModulus { get; }

    /// <summary>Poisson's ratio nu.</summary>
    public double PoissonsRatio { get; }

    /// <summary>Mass density (0 = weightless).</summary>
    public double Density { get; }

    /// <summary>Thermal conductivity k in the constitutive law <c>q = -k·grad T</c>
    /// (mW/(mm·K) for mm/N, numerically the SI W/(m·K)). Zero means "not stated".</summary>
    public double ThermalConductivity { get; }

    /// <summary>Specific heat capacity c (mm²/(s²·K) for mm/N/tonne/s, the SI J/(kg·K)
    /// times 1e6). Zero means "not stated".</summary>
    public double SpecificHeat { get; }

    /// <summary>Coefficient of linear thermal expansion alpha, in 1/K.</summary>
    public double ThermalExpansion { get; }

    /// <summary>Volumetric heat capacity <c>rho·c</c> — what a transient conduction solve
    /// actually integrates, and the product a caller should sanity-check (steel is 3.611
    /// mJ/(mm³·K)).</summary>
    public double VolumetricHeatCapacity => Density * SpecificHeat;

    /// <summary>Thermal diffusivity <c>k / (rho·c)</c>, in mm²/s — the number that sets a
    /// transient's own time scale (steel 13.85, aluminium 68.7). Zero when the volumetric
    /// heat capacity is zero, since a body with no capacity has no transient.</summary>
    public double ThermalDiffusivity
    {
        get
        {
            double capacity = VolumetricHeatCapacity;
            // Exact-zero division guard (the scale-free tier): "no capacity stated" is a
            // semantic case, not a small number to be protected against.
            return capacity == 0 ? 0 : ThermalConductivity / capacity;
        }
    }

    /// <summary>Lame's first parameter, L = E·nu / ((1+nu)(1-2nu)).</summary>
    public double Lambda { get; }

    /// <summary>Shear modulus (Lame's second parameter), M = E / (2(1+nu)).</summary>
    public double Mu { get; }

    /// <summary>Shear modulus G — the same number as <see cref="Mu"/>, under the name
    /// an engineer looks for.</summary>
    public double ShearModulus => Mu;

    /// <summary>Bulk modulus K = E / (3(1-2nu)).</summary>
    public double BulkModulus => YoungsModulus / (3.0 * (1.0 - 2.0 * PoissonsRatio));

    /// <summary>
    /// The 6x6 constitutive matrix D in row-major order, Voigt order
    /// (xx, yy, zz, xy, yz, zx) with engineering shear strains — the matrix in the class
    /// doc comment. Allocates, and is a convenience for inspection and tests: the element
    /// code reads <see cref="Lambda"/> and <see cref="Mu"/> directly rather than
    /// multiplying by a mostly-zero matrix.
    /// </summary>
    public double[] ConstitutiveMatrix()
    {
        var d = new double[36];
        double diagonal = Lambda + 2.0 * Mu;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
                d[i * 6 + j] = i == j ? diagonal : Lambda;
            d[(i + 3) * 6 + (i + 3)] = Mu;
        }
        return d;
    }

    /// <summary>
    /// Stress from strain: sigma = D·epsilon, both as Voigt 6-vectors with engineering
    /// shear strains. Written out rather than as a matrix product because D has three
    /// distinct entries and this is the solver's inner loop.
    /// </summary>
    public void Stress(ReadOnlySpan<double> strain, Span<double> stress)
    {
        double trace = strain[0] + strain[1] + strain[2];
        double lt = Lambda * trace;
        double twoMu = 2.0 * Mu;
        stress[0] = lt + twoMu * strain[0];
        stress[1] = lt + twoMu * strain[1];
        stress[2] = lt + twoMu * strain[2];
        stress[3] = Mu * strain[3];
        stress[4] = Mu * strain[4];
        stress[5] = Mu * strain[5];
    }

    /// <summary>The same material with a different density.</summary>
    public Material WithDensity(double density) =>
        new(Name, YoungsModulus, PoissonsRatio, density,
            ThermalConductivity, SpecificHeat, ThermalExpansion);

    /// <summary>
    /// The same material with thermal properties. Every <c>With…</c> here carries the
    /// OTHER properties over rather than defaulting them — a record's generated
    /// <c>with</c> cannot do that job, because these are get-only properties behind a
    /// validating constructor, and a version that silently dropped a conductivity would
    /// turn a working thermal model into a refusal three calls later.
    /// </summary>
    /// <param name="thermalConductivity">k, mW/(mm·K) — the SI W/(m·K).</param>
    /// <param name="specificHeat">c, mm²/(s²·K) — the SI J/(kg·K) times 1e6.</param>
    /// <param name="thermalExpansion">alpha, 1/K.</param>
    public Material WithThermal(
        double thermalConductivity, double specificHeat = 0, double thermalExpansion = 0) =>
        new(Name, YoungsModulus, PoissonsRatio, Density,
            thermalConductivity, specificHeat, thermalExpansion);

    /// <summary>The same material with a different expansion coefficient.</summary>
    public Material WithThermalExpansion(double thermalExpansion) =>
        new(Name, YoungsModulus, PoissonsRatio, Density,
            ThermalConductivity, SpecificHeat, thermalExpansion);

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name}: E = {YoungsModulus:G6}, nu = {PoissonsRatio:G4}, rho = {Density:G4}"
        + (ThermalConductivity > 0 ? $", k = {ThermalConductivity:G4}" : "")
        + (SpecificHeat > 0 ? $", c = {SpecificHeat:G4}" : "")
        + (ThermalExpansion > 0 ? $", alpha = {ThermalExpansion:G4}" : "");
}

/// <summary>
/// A small catalogue of common engineering materials, stated in the
/// <b>mm / N / MPa / tonne</b> unit system (see <see cref="Material"/>): E in MPa,
/// density in tonne/mm³, conductivity in mW/(mm·K) (the SI W/(m·K)), specific heat in
/// mm²/(s²·K) (the SI J/(kg·K) times 1e6) and expansion in 1/K.
///
/// <para><b>Nominal values, not certified data.</b> These are textbook room-temperature
/// figures for getting a model running; a real analysis takes its numbers from the
/// supplier's datasheet for the specific alloy and temper. They are here for the same
/// reason <c>StandardHoles</c>' tables are — so the common case needs no lookup — and
/// carry the same verify-against-datasheet caveat. The thermal figures deserve it more
/// than the elastic ones: conductivity varies by a factor of two across the stainless
/// grades, and a polymer's is sensitive to fillers and to print density.</para>
/// </summary>
public static class Materials
{
    /// <summary>Structural steel: E = 210 GPa, nu = 0.30, rho = 7850 kg/m³,
    /// k = 50 W/(m·K), c = 460 J/(kg·K), alpha = 12e-6 /K.</summary>
    public static Material Steel { get; } =
        new("Structural steel", 210_000, 0.30, 7.85e-9, 50.0, 4.60e8, 12.0e-6);

    /// <summary>Stainless steel 304: E = 193 GPa, nu = 0.29, rho = 8000 kg/m³,
    /// k = 16.2 W/(m·K), c = 500 J/(kg·K), alpha = 17.3e-6 /K.</summary>
    public static Material StainlessSteel304 { get; } =
        new("Stainless steel 304", 193_000, 0.29, 8.00e-9, 16.2, 5.00e8, 17.3e-6);

    /// <summary>Aluminium 6061-T6: E = 68.9 GPa, nu = 0.33, rho = 2700 kg/m³,
    /// k = 167 W/(m·K), c = 896 J/(kg·K), alpha = 23.6e-6 /K.</summary>
    public static Material Aluminium6061 { get; } =
        new("Aluminium 6061-T6", 68_900, 0.33, 2.70e-9, 167.0, 8.96e8, 23.6e-6);

    /// <summary>Aluminium 7075-T6: E = 71.7 GPa, nu = 0.33, rho = 2810 kg/m³,
    /// k = 130 W/(m·K), c = 960 J/(kg·K), alpha = 23.6e-6 /K.</summary>
    public static Material Aluminium7075 { get; } =
        new("Aluminium 7075-T6", 71_700, 0.33, 2.81e-9, 130.0, 9.60e8, 23.6e-6);

    /// <summary>Titanium Ti-6Al-4V: E = 113.8 GPa, nu = 0.342, rho = 4430 kg/m³,
    /// k = 6.7 W/(m·K), c = 526 J/(kg·K), alpha = 8.6e-6 /K.</summary>
    public static Material Titanium6Al4V { get; } =
        new("Titanium Ti-6Al-4V", 113_800, 0.342, 4.43e-9, 6.7, 5.26e8, 8.6e-6);

    /// <summary>Grey cast iron: E = 110 GPa, nu = 0.26, rho = 7200 kg/m³,
    /// k = 46 W/(m·K), c = 460 J/(kg·K), alpha = 10.5e-6 /K.</summary>
    public static Material CastIron { get; } =
        new("Grey cast iron", 110_000, 0.26, 7.20e-9, 46.0, 4.60e8, 10.5e-6);

    /// <summary>Brass (C36000): E = 97 GPa, nu = 0.31, rho = 8500 kg/m³,
    /// k = 115 W/(m·K), c = 380 J/(kg·K), alpha = 20.5e-6 /K.</summary>
    public static Material Brass { get; } =
        new("Brass C36000", 97_000, 0.31, 8.50e-9, 115.0, 3.80e8, 20.5e-6);

    /// <summary>ABS (injection moulded): E = 2.3 GPa, nu = 0.35, rho = 1040 kg/m³,
    /// k = 0.17 W/(m·K), c = 1400 J/(kg·K), alpha = 90e-6 /K.</summary>
    public static Material Abs { get; } =
        new("ABS", 2_300, 0.35, 1.04e-9, 0.17, 1.40e9, 90.0e-6);

    /// <summary>PLA (3D printed, bulk approximation): E = 3.5 GPa, nu = 0.36, rho = 1250 kg/m³,
    /// k = 0.13 W/(m·K), c = 1800 J/(kg·K), alpha = 68e-6 /K.
    /// A printed part is anisotropic and layer-bonded; this is the bulk figure and will
    /// overestimate strength across layers — and, for the same reason, overestimate
    /// conduction across them.</summary>
    public static Material Pla { get; } =
        new("PLA", 3_500, 0.36, 1.25e-9, 0.13, 1.80e9, 68.0e-6);

    /// <summary>Nylon 6/6: E = 2.0 GPa, nu = 0.39, rho = 1140 kg/m³,
    /// k = 0.25 W/(m·K), c = 1700 J/(kg·K), alpha = 80e-6 /K.</summary>
    public static Material Nylon { get; } =
        new("Nylon 6/6", 2_000, 0.39, 1.14e-9, 0.25, 1.70e9, 80.0e-6);

    /// <summary>Every catalogue entry: the metals in declaration order, then the
    /// polymers.</summary>
    public static IReadOnlyList<Material> All { get; } =
    [
        Steel, StainlessSteel304, Aluminium6061, Aluminium7075, Titanium6Al4V,
        CastIron, Brass, Abs, Nylon, Pla,
    ];

    /// <summary>Standard gravity pointing along -Z, in mm/s² (the mm/N/MPa/tonne system).
    /// Pass it to <c>StructuralModel.Gravity</c>.</summary>
    public static Vector3d GravityMillimetres { get; } = new(0, 0, -9806.65);

    /// <summary>Standard gravity pointing along -Z, in m/s² (the SI system).</summary>
    public static Vector3d GravityMetres { get; } = new(0, 0, -9.80665);
}
