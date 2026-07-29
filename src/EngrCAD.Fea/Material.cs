using EngrCAD.Core;

namespace EngrCAD.Fea;

/// <summary>
/// An isotropic linear-elastic material: Young's modulus, Poisson's ratio and mass
/// density, with the derived Lame parameters every element formulation actually uses.
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
    public Material(string name, double youngsModulus, double poissonsRatio, double density = 0)
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

        Name = name;
        YoungsModulus = youngsModulus;
        PoissonsRatio = poissonsRatio;
        Density = density;

        Mu = youngsModulus / (2.0 * (1.0 + poissonsRatio));
        Lambda = youngsModulus * poissonsRatio / ((1.0 + poissonsRatio) * (1.0 - 2.0 * poissonsRatio));
    }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Young's modulus E.</summary>
    public double YoungsModulus { get; }

    /// <summary>Poisson's ratio nu.</summary>
    public double PoissonsRatio { get; }

    /// <summary>Mass density (0 = weightless).</summary>
    public double Density { get; }

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
        new(Name, YoungsModulus, PoissonsRatio, density);

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name}: E = {YoungsModulus:G6}, nu = {PoissonsRatio:G4}, rho = {Density:G4}";
}

/// <summary>
/// A small catalogue of common engineering materials, stated in the
/// <b>mm / N / MPa / tonne</b> unit system (see <see cref="Material"/>): E in MPa,
/// density in tonne/mm³.
///
/// <para><b>Nominal values, not certified data.</b> These are textbook room-temperature
/// figures for getting a model running; a real analysis takes its numbers from the
/// supplier's datasheet for the specific alloy and temper. They are here for the same
/// reason <c>StandardHoles</c>' tables are — so the common case needs no lookup — and
/// carry the same verify-against-datasheet caveat.</para>
/// </summary>
public static class Materials
{
    /// <summary>Structural steel: E = 210 GPa, nu = 0.30, rho = 7850 kg/m³.</summary>
    public static Material Steel { get; } = new("Structural steel", 210_000, 0.30, 7.85e-9);

    /// <summary>Stainless steel 304: E = 193 GPa, nu = 0.29, rho = 8000 kg/m³.</summary>
    public static Material StainlessSteel304 { get; } = new("Stainless steel 304", 193_000, 0.29, 8.00e-9);

    /// <summary>Aluminium 6061-T6: E = 68.9 GPa, nu = 0.33, rho = 2700 kg/m³.</summary>
    public static Material Aluminium6061 { get; } = new("Aluminium 6061-T6", 68_900, 0.33, 2.70e-9);

    /// <summary>Aluminium 7075-T6: E = 71.7 GPa, nu = 0.33, rho = 2810 kg/m³.</summary>
    public static Material Aluminium7075 { get; } = new("Aluminium 7075-T6", 71_700, 0.33, 2.81e-9);

    /// <summary>Titanium Ti-6Al-4V: E = 113.8 GPa, nu = 0.342, rho = 4430 kg/m³.</summary>
    public static Material Titanium6Al4V { get; } = new("Titanium Ti-6Al-4V", 113_800, 0.342, 4.43e-9);

    /// <summary>Grey cast iron: E = 110 GPa, nu = 0.26, rho = 7200 kg/m³.</summary>
    public static Material CastIron { get; } = new("Grey cast iron", 110_000, 0.26, 7.20e-9);

    /// <summary>Brass (C36000): E = 97 GPa, nu = 0.31, rho = 8500 kg/m³.</summary>
    public static Material Brass { get; } = new("Brass C36000", 97_000, 0.31, 8.50e-9);

    /// <summary>ABS (injection moulded): E = 2.3 GPa, nu = 0.35, rho = 1040 kg/m³.</summary>
    public static Material Abs { get; } = new("ABS", 2_300, 0.35, 1.04e-9);

    /// <summary>PLA (3D printed, bulk approximation): E = 3.5 GPa, nu = 0.36, rho = 1250 kg/m³.
    /// A printed part is anisotropic and layer-bonded; this is the bulk figure and will
    /// overestimate strength across layers.</summary>
    public static Material Pla { get; } = new("PLA", 3_500, 0.36, 1.25e-9);

    /// <summary>Nylon 6/6: E = 2.0 GPa, nu = 0.39, rho = 1140 kg/m³.</summary>
    public static Material Nylon { get; } = new("Nylon 6/6", 2_000, 0.39, 1.14e-9);

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
