namespace EngrCAD.Core;

/// <summary>
/// A material: a name, a mass density and an optional display color — plus the optional
/// analysis properties (Young's modulus, Poisson's ratio, conductivity, specific heat,
/// expansion) a simulation needs, and the Lame parameters an element formulation derives
/// from them.
///
/// <para><b>One type serves the document model and the simulation layer</b>, which is why it
/// lives in <c>EngrCAD.Core</c> — the only assembly both <c>EngrCAD.Modeling</c> and
/// <c>EngrCAD.Fea</c> see. <c>Part.Material</c> and a structural solve are asking about the
/// same physical stuff, and two types would mean two densities, which is exactly the
/// discrepancy this consolidation removed (see the unit note below).</para>
///
/// <para><b>The analysis properties are OPTIONAL, and zero means "not stated".</b> A material
/// with a density and no modulus is a perfectly good document material — most of a bill of
/// materials is made of them — so the refusal lives at the point of use, where an analysis
/// can name the property it needs: <c>StructuralModel</c> refuses a material with no Young's
/// modulus, <c>ThermalSolver</c> one with no conductivity, <c>ModalSolver</c> one with no
/// density. Building the material cannot know which analysis is coming, and refusing there
/// would make a BOM entry require an elastic modulus.</para>
///
/// <para><b>Units are the consistent mm / N / MPa / tonne / s system</b> that
/// <see cref="ModelUnits"/> states once for the whole repository: E in MPa, <b>density in
/// tonne/mm³</b> (structural steel 7.85e-9 — use
/// <see cref="ModelUnits.DensityFromKilogramsPerCubicMetre"/> when transcribing a datasheet's
/// kg/m³, and <see cref="DensityKilogramsPerCubicMetre"/> to read it back), conductivity in
/// mW/(mm·K), specific heat in mm²/(s²·K), expansion in 1/K. A mass computed from this
/// density is therefore in <b>tonnes</b>; <see cref="ModelUnits.MassToKilograms"/> and
/// <see cref="ModelUnits.MassToGrams"/> are how a report prints it. The SI alternative
/// (m / N / Pa / kg) is equally consistent; mixing the two is what nothing here can catch.</para>
///
/// <para><b>The thermal units are worth stating, because two of the three surprise people.</b>
/// With mm / N / tonne / s, energy is N·mm = mJ and power is mJ/s = mW, so:
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
    /// Builds a material. Only the name is required: a document material is
    /// <c>new Material("Delrin", density: 1.41e-9)</c>, and every analysis property left at
    /// its zero default means "not stated" and is refused by whichever analysis needs it,
    /// by name.
    ///
    /// <para><paramref name="poissonsRatio"/> must lie in (-1, 0.5) whenever it is stated: at
    /// 0.5 the material is incompressible and Lame's first parameter diverges, which a
    /// displacement-based formulation cannot represent (it locks). The bound is checked
    /// rather than clamped, because a value at or past it is a modelling error and a
    /// silently adjusted one would produce a stiffness nobody asked for. Note that its zero
    /// default is a legal value as well as the unstated one — state it whenever you state a
    /// modulus, since <see cref="HasElasticity"/> reads the modulus alone.</para>
    /// </summary>
    /// <param name="name">Display name (appears in reports and refusal messages).</param>
    /// <param name="youngsModulus">E, in the model's stress unit (MPa). Zero means "not
    /// stated": the material has no elastic properties and a structural solve refuses it.</param>
    /// <param name="poissonsRatio">nu, dimensionless, in (-1, 0.5).</param>
    /// <param name="density">Mass density in tonne/mm³ (steel 7.85e-9). Zero is legal and
    /// means body loads contribute nothing and a mass is unknown.</param>
    /// <param name="thermalConductivity">k, in mW/(mm·K) — numerically the SI W/(m·K).
    /// Zero means "not stated", and a thermal solve refuses such a material by name rather
    /// than assembling a singular matrix.</param>
    /// <param name="specificHeat">c, in mm²/(s²·K) — the SI J/(kg·K) times 1e6. Needed
    /// only by a transient solve; zero means "not stated" and is refused there.</param>
    /// <param name="thermalExpansion">alpha, in 1/K. Zero is legal and means a temperature
    /// change produces no thermal-expansion load.</param>
    /// <param name="color">Display color. Null means the document model picks one from its
    /// palette, which is what every catalogue entry here does deliberately — a material's
    /// appearance is a finish, not a physical property.</param>
    public Material(
        string name,
        double youngsModulus = 0,
        double poissonsRatio = 0,
        double density = 0,
        double thermalConductivity = 0,
        double specificHeat = 0,
        double thermalExpansion = 0,
        PartColor? color = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A material needs a non-empty name.", nameof(name));
        if (!(youngsModulus >= 0) || double.IsInfinity(youngsModulus))
            throw new ArgumentOutOfRangeException(nameof(youngsModulus), youngsModulus,
                $"Young's modulus must be finite and non-negative; '{name}' was given {youngsModulus:G6}. "
                + "Zero means 'not stated' — a document material with no elastic properties — "
                + "and is refused by a structural solve rather than here.");
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
        Color = color;

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

    /// <summary>Young's modulus E in MPa; zero means "not stated"
    /// (see <see cref="HasElasticity"/>).</summary>
    public double YoungsModulus { get; }

    /// <summary>Poisson's ratio nu.</summary>
    public double PoissonsRatio { get; }

    /// <summary>Mass density in <b>tonne/mm³</b> — steel 7.85e-9, not 7850 (see
    /// <see cref="ModelUnits"/>). Zero means unknown, which for a static solve simply means
    /// weightless.</summary>
    public double Density { get; }

    /// <summary>The same density as the datasheet's kg/m³ (steel 7850) — the spelling to
    /// print when a human has to check the number, never the one to compute with.</summary>
    public double DensityKilogramsPerCubicMetre =>
        ModelUnits.DensityToKilogramsPerCubicMetre(Density);

    /// <summary>Thermal conductivity k in the constitutive law <c>q = -k·grad T</c>
    /// (mW/(mm·K), numerically the SI W/(m·K)). Zero means "not stated".</summary>
    public double ThermalConductivity { get; }

    /// <summary>Specific heat capacity c (mm²/(s²·K), the SI J/(kg·K) times 1e6). Zero
    /// means "not stated".</summary>
    public double SpecificHeat { get; }

    /// <summary>Coefficient of linear thermal expansion alpha, in 1/K.</summary>
    public double ThermalExpansion { get; }

    /// <summary>Display color, or null to let the document model's palette choose.</summary>
    public PartColor? Color { get; }

    /// <summary>
    /// True when the material states a Young's modulus, i.e. when a structural, modal or
    /// buckling solve can use it. The modulus alone is the test: Poisson's ratio has a legal
    /// value of zero, so it cannot distinguish "not stated" from "stated as zero", whereas a
    /// solid with no stiffness at all is not a material.
    /// </summary>
    public bool HasElasticity => YoungsModulus > 0;

    /// <summary>Volumetric heat capacity <c>rho·c</c> — what a transient conduction solve
    /// actually integrates, and the product a caller should sanity-check (steel is 3.611
    /// mJ/(mm³·K)).</summary>
    public double VolumetricHeatCapacity => Density * SpecificHeat;

    /// <summary>Thermal diffusivity <c>k / (rho·c)</c>, in mm²/s — the number that sets a
    /// transient's own time scale (steel 13.85, aluminium 69.03, both computed from this
    /// catalogue's own rows rather than quoted). Zero when the volumetric
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

    /// <summary>Lame's first parameter, L = E·nu / ((1+nu)(1-2nu)). Zero for a material with
    /// no modulus — which is why <c>StructuralModel</c> refuses one before it can be used
    /// as a silent zero stiffness.</summary>
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

    /// <summary>The same material with a different density (tonne/mm³).</summary>
    public Material WithDensity(double density) =>
        new(Name, YoungsModulus, PoissonsRatio, density,
            ThermalConductivity, SpecificHeat, ThermalExpansion, Color);

    /// <summary>
    /// The same material with elastic properties — the call that turns a document material
    /// into one a structural solve accepts.
    /// </summary>
    /// <param name="youngsModulus">E in MPa.</param>
    /// <param name="poissonsRatio">nu, in (-1, 0.5).</param>
    public Material WithElasticity(double youngsModulus, double poissonsRatio) =>
        new(Name, youngsModulus, poissonsRatio, Density,
            ThermalConductivity, SpecificHeat, ThermalExpansion, Color);

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
            thermalConductivity, specificHeat, thermalExpansion, Color);

    /// <summary>The same material with a different expansion coefficient.</summary>
    public Material WithThermalExpansion(double thermalExpansion) =>
        new(Name, YoungsModulus, PoissonsRatio, Density,
            ThermalConductivity, SpecificHeat, thermalExpansion, Color);

    /// <summary>The same material with a display color — the finish, not the physics, which
    /// is why the catalogue leaves it null and this is a separate call.</summary>
    public Material WithColor(PartColor? color) =>
        new(Name, YoungsModulus, PoissonsRatio, Density,
            ThermalConductivity, SpecificHeat, ThermalExpansion, color);

    /// <summary>The same material under a different name — a renamed alloy or temper that
    /// carries the same numbers.</summary>
    public Material WithName(string name) =>
        new(name, YoungsModulus, PoissonsRatio, Density,
            ThermalConductivity, SpecificHeat, ThermalExpansion, Color);

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name}: rho = {Density:G4}"
        + (HasElasticity ? $", E = {YoungsModulus:G6}, nu = {PoissonsRatio:G4}" : "")
        + (ThermalConductivity > 0 ? $", k = {ThermalConductivity:G4}" : "")
        + (SpecificHeat > 0 ? $", c = {SpecificHeat:G4}" : "")
        + (ThermalExpansion > 0 ? $", alpha = {ThermalExpansion:G4}" : "");
}

/// <summary>
/// A small catalogue of common engineering materials, stated in the
/// <b>mm / N / MPa / tonne</b> unit system (see <see cref="ModelUnits"/>): E in MPa,
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
///
/// <para><b>No catalogue entry carries a display color</b>, deliberately: appearance is a
/// finish (anodised, painted, as-cast) rather than a property of the stuff, so assigning
/// one of these to a part leaves its color exactly where the palette put it and moves no
/// pixels. <c>Material.WithColor</c> attaches one when a design wants it.</para>
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

    /// <summary>Stainless steel 316: E = 193 GPa, nu = 0.29, rho = 8000 kg/m³,
    /// k = 16.3 W/(m·K), c = 500 J/(kg·K), alpha = 16.0e-6 /K. The molybdenum-bearing
    /// grade (X5CrNiMo17-12-2 / 1.4401), and the alloy ISO 3506 calls <b>A4</b> in a
    /// fastener catalogue.</summary>
    public static Material StainlessSteel316 { get; } =
        new("Stainless steel 316", 193_000, 0.29, 8.00e-9, 16.3, 5.00e8, 16.0e-6);

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
        Steel, StainlessSteel304, StainlessSteel316, Aluminium6061, Aluminium7075,
        Titanium6Al4V, CastIron, Brass, Abs, Nylon, Pla,
    ];

    /// <summary>Standard gravity pointing along -Z, in mm/s² (the mm/N/MPa/tonne system).
    /// Pass it to <c>StructuralModel.Gravity</c>. The same vector as
    /// <see cref="ModelUnits.Gravity"/>, which is where the unit system is stated.</summary>
    public static Vector3d GravityMillimetres => ModelUnits.Gravity;

    /// <summary>Standard gravity pointing along -Z, in m/s² (the SI system).</summary>
    public static Vector3d GravityMetres { get; } = new(0, 0, -9.80665);
}
