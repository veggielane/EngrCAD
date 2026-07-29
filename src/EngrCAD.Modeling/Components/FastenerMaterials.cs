using EngrCAD.Core;

namespace EngrCAD.Modeling;

/// <summary>
/// What the standard components are <b>made of</b> — the material catalogue behind
/// <see cref="HardwareComponent.Material"/>, so that a bill of materials of bought-in
/// parts weighs itself.
///
/// <para><b>The field carries the STUFF, not the strength grade — and that distinction is
/// the whole trap here.</b> ISO 898-1 property classes (8.8, 10.9, 12.9) name a
/// <i>proof</i> and <i>tensile</i> stress, not a substance: an M6×20 cap screw weighs the
/// same whether it is 8.8 or 12.9, because both are steel at 7850 kg/m³ and the classes
/// differ in heat treatment and alloy content, which moves the density by well under a
/// percent. So a class does NOT get its own <see cref="Material"/>; it belongs to the
/// component's designation and its allowable load, neither of which this type carries.
/// What genuinely does change the mass is a change of substance — a stainless A2 screw is
/// ~0.6% heavier than a carbon-steel one and a brass insert 8% denser again — and those
/// are the entries below.</para>
///
/// <para><b>Nothing here is a second catalogue.</b> Where <see cref="Materials"/> already
/// states the alloy, these entries <i>delegate</i> to it and only rename
/// (<see cref="StainlessA2"/> is <see cref="Materials.StainlessSteel304"/> under the
/// fastener designation ISO 3506 uses for it), because two spellings of one density is
/// exactly the discrepancy the material consolidation removed. Densities are in
/// <b>tonne/mm³</b>, the mm / N / MPa / tonne / s system <see cref="ModelUnits"/> states
/// once for the repository.</para>
///
/// <para><b>⚠ Transcribed, not certified.</b> Like <c>StandardHoles</c>' and
/// <c>StandardComponents</c>' dimension tables, these are nominal room-temperature figures
/// for getting a mass and a stiffness in the right place. Verify against the supplier's
/// datasheet for the specific grade before production use — a supplier's "stainless" may
/// be any of half a dozen alloys.</para>
///
/// <para><b>No entry carries a display colour</b>, following the rule
/// <see cref="Materials"/> already states: appearance is a finish, not a property of the
/// stuff, and a material colour does not consume a palette slot. A component's own
/// <see cref="HardwareComponent.Color"/> stays the appearance decision, so attaching these
/// materials moved no pixels.</para>
/// </summary>
public static class FastenerMaterials
{
    /// <summary>
    /// Plain carbon steel — ISO 898-1 property classes 4.6 to 8.8, ISO 4032 class-8 nuts,
    /// ISO 7089 200 HV washers and ISO 2338 pins. rho = 7850 kg/m³, E = 205 GPa (the
    /// figure ISO 898-1 uses for calculation, slightly below structural steel's 210),
    /// nu = 0.30, k = 50 W/(m·K), c = 460 J/(kg·K), alpha = 12e-6 /K.
    /// </summary>
    public static Material CarbonSteel { get; } =
        new("Carbon steel", 205_000, 0.30, ModelUnits.DensityFromKilogramsPerCubicMetre(7850),
            50.0, 4.60e8, 12.0e-6);

    /// <summary>
    /// Through-hardened low-alloy steel (34CrMo4 / 42CrMo4 and relatives) — ISO 898-1
    /// property classes 9.8 to 12.9, which is what a socket-head, button-head or
    /// countersunk screw normally is. <b>The same 7850 kg/m³ as
    /// <see cref="CarbonSteel"/></b>: the alloying moves the strength, not the mass, which
    /// is why the two differ here only in name, modulus and conductivity.
    /// E = 205 GPa, nu = 0.30, k = 42 W/(m·K), c = 460 J/(kg·K), alpha = 11.5e-6 /K.
    /// </summary>
    public static Material AlloySteel { get; } =
        new("Alloy steel", 205_000, 0.30, ModelUnits.DensityFromKilogramsPerCubicMetre(7850),
            42.0, 4.60e8, 11.5e-6);

    /// <summary>
    /// Austenitic stainless, ISO 3506 group <b>A2</b> — X5CrNi18-10 (1.4301), the alloy
    /// <see cref="Materials.StainlessSteel304"/> already states, under the designation a
    /// fastener catalogue prints. Delegating rather than restating is the point: one
    /// density for one alloy, whichever name asks for it.
    /// </summary>
    public static Material StainlessA2 { get; } =
        Materials.StainlessSteel304.WithName("Stainless steel A2 (1.4301)");

    /// <summary>
    /// Austenitic stainless, ISO 3506 group <b>A4</b> — the molybdenum-bearing
    /// X5CrNiMo17-12-2 (1.4401 / 316) used where A2 would pit. Delegates to
    /// <see cref="Materials.StainlessSteel316"/> for the same reason
    /// <see cref="StainlessA2"/> does.
    /// </summary>
    public static Material StainlessA4 { get; } =
        Materials.StainlessSteel316.WithName("Stainless steel A4 (1.4401)");

    /// <summary>
    /// Free-machining brass (CuZn39Pb3 / CZ121, the C36000 <see cref="Materials.Brass"/>
    /// already states) — threaded inserts for plastics, at 8500 kg/m³ the densest thing in
    /// this catalogue. Delegates, and does not even rename: "Brass C36000" is what a
    /// purchasing view wants to read.
    /// </summary>
    public static Material Brass => Materials.Brass;

    /// <summary>
    /// Through-hardened bearing steel, 100Cr6 / AISI 52100 — rho = 7810 kg/m³ (measurably
    /// below plain steel's 7850: the chromium content shows up), E = 210 GPa, nu = 0.30,
    /// k = 45 W/(m·K), c = 460 J/(kg·K), alpha = 11.9e-6 /K.
    ///
    /// <para>Offered but <b>not</b> attached to <see cref="DeepGrooveBearing"/> by default,
    /// and the reason is in that class: a v1 bearing body models two rings and neither the
    /// balls nor the cage, so a density times its volume is not the bearing's mass. An
    /// unstated material reports an honest "unknown" in a bill of materials, where a stated
    /// one would report a confidently light number.</para>
    /// </summary>
    public static Material BearingSteel { get; } =
        new("Bearing steel 100Cr6", 210_000, 0.30,
            ModelUnits.DensityFromKilogramsPerCubicMetre(7810), 45.0, 4.60e8, 11.9e-6);

    /// <summary>Every entry, in declaration order (the steels, then the stainlesses, then
    /// brass and bearing steel) — for a picker, and for the test that asserts each
    /// density against its datasheet figure in kg/m³.</summary>
    public static IReadOnlyList<Material> All { get; } =
    [
        CarbonSteel, AlloySteel, StainlessA2, StainlessA4, Brass, BearingSteel,
    ];
}
