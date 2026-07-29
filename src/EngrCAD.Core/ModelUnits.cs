namespace EngrCAD.Core;

/// <summary>
/// <b>The unit system an EngrCAD model is stated in, and the conversions in and out of it.</b>
/// This type is the single place the convention is written down; everything else — the
/// <see cref="Material"/> catalogue, mass properties, the FEA solvers, the sheet-metal and
/// hole tables — cross-references it rather than restating it.
///
/// <para><b>The system is mm / N / MPa / tonne / s</b> (Abaqus calls it exactly that, and it
/// is what STEP export already assumes, since STEP writes millimetres). Lengths are in
/// millimetres, forces in newtons, stresses in MPa = N/mm², <b>densities in tonne/mm³</b>
/// (structural steel is <c>7.85e-9</c>, not 7850 and not 7.85e-6), time in seconds and
/// gravity <c>9806.65</c> mm/s².</para>
///
/// <para><b>Why the density is stated in tonne/mm³ rather than in the kilograms a bill of
/// materials wants.</b> A density is either a number an <i>equation</i> consumes or a number
/// a <i>report</i> prints, and only one of those can be converted after the fact. The FEA
/// assembly multiplies density into a mass matrix that has to balance against a stiffness in
/// MPa and a length in mm — there is nowhere to put a conversion factor, because the factor
/// would have to reappear in the gravity load, the heat capacity, the natural frequency, the
/// buckling load and the transient time constant, every one of which is verified here against
/// a closed form. Mass properties, by contrast, form exactly ONE product from the density
/// (mass = density x volume), so a report can convert that single number at the end.
/// <b>The convention therefore lives where it cannot be converted, and the readable units are
/// accessors</b> — <see cref="MassToKilograms"/>, <see cref="MassToGrams"/>,
/// <see cref="DensityToKilogramsPerCubicMetre"/> — rather than a second convention.</para>
///
/// <para>Nothing in this kernel carries a unit at runtime, so none of this can be checked:
/// the system is documented and converted at the edges, never enforced. The SI alternative
/// (m / N / Pa / kg) is equally consistent and works identically; what does not work is
/// mixing them, which is exactly what these conversions exist to stop.</para>
/// </summary>
public static class ModelUnits
{
    /// <summary>
    /// The factor taking a datasheet density in kg/m³ to this system's tonne/mm³:
    /// <c>1e-12</c> (a tonne is 1e3 kg and a cubic metre is 1e9 mm³).
    /// </summary>
    public const double TonnesPerCubicMillimetrePerKilogramPerCubicMetre = 1e-12;

    /// <summary>
    /// A datasheet density in kg/m³ as this system's tonne/mm³ — the call to make when
    /// transcribing a supplier's figure, so nobody has to type the exponent
    /// (<c>DensityFromKilogramsPerCubicMetre(7850)</c> is steel).
    /// </summary>
    public static double DensityFromKilogramsPerCubicMetre(double kilogramsPerCubicMetre) =>
        kilogramsPerCubicMetre * TonnesPerCubicMillimetrePerKilogramPerCubicMetre;

    /// <summary>
    /// A model density (tonne/mm³) back as the datasheet's kg/m³ — what to print when a
    /// human has to check the number against a supplier's page.
    /// </summary>
    public static double DensityToKilogramsPerCubicMetre(double tonnesPerCubicMillimetre) =>
        tonnesPerCubicMillimetre / TonnesPerCubicMillimetrePerKilogramPerCubicMetre;

    /// <summary>A model mass (tonnes, being tonne/mm³ x mm³) in kilograms.</summary>
    public static double MassToKilograms(double tonnes) => tonnes * 1e3;

    /// <summary>A model mass (tonnes) in grams — the readable unit for a typical CAD part,
    /// which weighs tens of grams.</summary>
    public static double MassToGrams(double tonnes) => tonnes * 1e6;

    /// <summary>A mass in kilograms as a model mass (tonnes).</summary>
    public static double MassFromKilograms(double kilograms) => kilograms * 1e-3;

    /// <summary>Standard gravity along -Z in this system, <c>9806.65</c> mm/s².
    /// <see cref="Materials.GravityMillimetres"/> is the same vector under the name the
    /// simulation catalogue uses.</summary>
    public static Vector3d Gravity { get; } = new(0, 0, -9806.65);
}
