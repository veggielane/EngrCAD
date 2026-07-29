using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// What the catalogue components are made of, and the one user-visible payoff: a bill of
/// materials of bought-in parts that weighs itself.
///
/// <para>Densities are asserted in <b>kg/m³</b> throughout — the datasheet figure, which
/// is the only form a human can check. Comparing <c>7.85e-9</c> against <c>7.85e-9</c>
/// would verify typing rather than physics (design.md §2's rule for the
/// <see cref="Materials"/> catalogue, which applies here for the same reason).</para>
/// </summary>
public class HardwareMaterialTests
{
    private static void AssertDensity(double kilogramsPerCubicMetre, Material material) =>
        Assert.Equal(kilogramsPerCubicMetre, material.DensityKilogramsPerCubicMetre, 6);

    // ---- the catalogue itself ----

    [Fact]
    public void FastenerMaterials_StateTheirDatasheetDensities()
    {
        AssertDensity(7850, FastenerMaterials.CarbonSteel);
        AssertDensity(7850, FastenerMaterials.AlloySteel);
        AssertDensity(8000, FastenerMaterials.StainlessA2);
        AssertDensity(8000, FastenerMaterials.StainlessA4);
        AssertDensity(8500, FastenerMaterials.Brass);
        AssertDensity(7810, FastenerMaterials.BearingSteel);
    }

    /// <summary>
    /// The trap the field exists to avoid: an ISO 898-1 property class is a STRENGTH
    /// grade, not a substance. A 12.9 screw and an 8.8 screw of one size weigh the same,
    /// so the two steels differ in name, modulus and conductivity — and not by one bit in
    /// density.
    /// </summary>
    [Fact]
    public void PropertyClassIsStrength_NotDensity()
    {
        Assert.Equal(FastenerMaterials.CarbonSteel.Density, FastenerMaterials.AlloySteel.Density);
        Assert.NotEqual(FastenerMaterials.CarbonSteel.Name, FastenerMaterials.AlloySteel.Name);
        Assert.NotEqual(
            FastenerMaterials.CarbonSteel.ThermalConductivity,
            FastenerMaterials.AlloySteel.ThermalConductivity);
    }

    /// <summary>
    /// A change of SUBSTANCE is what moves a mass, which is the other half of the same
    /// point: stainless is measurably heavier than carbon steel and brass heavier again.
    /// </summary>
    [Fact]
    public void ChangingTheSubstanceChangesTheMass()
    {
        Assert.True(FastenerMaterials.StainlessA2.Density > FastenerMaterials.CarbonSteel.Density);
        Assert.True(FastenerMaterials.Brass.Density > FastenerMaterials.StainlessA4.Density);
        // ~1.9% for stainless, ~8% for brass - small, but not a rounding.
        double stainlessRatio =
            FastenerMaterials.StainlessA2.Density / FastenerMaterials.CarbonSteel.Density;
        Assert.InRange(stainlessRatio, 1.015, 1.025);
    }

    /// <summary>
    /// Not a second catalogue: where <see cref="Materials"/> already states the alloy, the
    /// fastener entry renames it and nothing else. Asserted as bit equality, because two
    /// spellings of one density is exactly the discrepancy the material consolidation
    /// removed.
    /// </summary>
    [Fact]
    public void StainlessEntriesDelegateToTheCoreCatalogue()
    {
        Assert.Equal(Materials.StainlessSteel304.Density, FastenerMaterials.StainlessA2.Density);
        Assert.Equal(Materials.StainlessSteel304.YoungsModulus, FastenerMaterials.StainlessA2.YoungsModulus);
        Assert.Equal(Materials.StainlessSteel316.Density, FastenerMaterials.StainlessA4.Density);
        Assert.Same(Materials.Brass, FastenerMaterials.Brass);
        // The rename is the only difference, and it is a real one (ISO 3506 spells it A2).
        Assert.Contains("A2", FastenerMaterials.StainlessA2.Name);
        Assert.Contains("A4", FastenerMaterials.StainlessA4.Name);
    }

    /// <summary>A catalogue material carries no colour: appearance is a finish, not a
    /// property of the stuff — so attaching these moved no pixels.</summary>
    [Fact]
    public void NoFastenerMaterialCarriesAColour()
    {
        foreach (var material in FastenerMaterials.All)
            Assert.Null(material.Color);
    }

    // ---- the components ----

    public static TheoryData<HardwareComponent, string> WithMaterial() => new()
    {
        { StandardComponents.CapScrew(6, 20), "Alloy steel" },
        { StandardComponents.ButtonScrew(6, 16), "Alloy steel" },
        { StandardComponents.CskScrew(6, 16), "Alloy steel" },
        { StandardComponents.Nut(6), "Carbon steel" },
        { StandardComponents.Washer(6), "Carbon steel" },
        { StandardComponents.Dowel(6, 20), "Carbon steel" },
        { StandardComponents.TrisertInsert(6), "Brass C36000" },
    };

    [Theory]
    [MemberData(nameof(WithMaterial))]
    public void EveryFastenerStatesWhatItIsMadeOf(HardwareComponent component, string expected)
    {
        Assert.NotNull(component.Material);
        Assert.Equal(expected, component.Material!.Name);
        // ToPart carries it onto the document-model part, which is what a BOM reads.
        Assert.Same(component.Material, component.ToPart().Material);
    }

    /// <summary>The material must not disturb the appearance a component already chose:
    /// <see cref="HardwareComponent.ToPart"/> sets an explicit colour, and no catalogue
    /// material carries one, so both paths agree that nothing moved.</summary>
    [Theory]
    [MemberData(nameof(WithMaterial))]
    public void StatingAMaterialLeavesTheColourAlone(HardwareComponent component, string expected)
    {
        _ = expected;
        Assert.Null(component.Material!.Color);
        Assert.Equal(component.Color, component.ToPart().Color);
    }

    /// <summary>
    /// The deliberate omission, pinned so it cannot be "fixed" by accident: a v1 bearing
    /// models two rings and neither the balls nor the cage, so density times volume is
    /// less than the bearing's real mass. An unstated material reports "unknown"; a stated
    /// one would report the shortfall as a number.
    /// </summary>
    [Fact]
    public void ABearingStatesNoMaterial_BecauseItsBodyIsNotItsWholeGeometry()
    {
        var bearing = StandardComponents.Bearing("608");
        Assert.Null(bearing.Material);
        Assert.Null(bearing.ToPart().Material);
        Assert.Null(bearing.ToPart().MassGrams());

        // ...and the escape hatch works: one part per component, so one assignment covers
        // every occurrence.
        var part = StandardComponents.Bearing("608").ToPart().Of(FastenerMaterials.BearingSteel);
        Assert.NotNull(part.MassGrams());
        Assert.True(part.MassGrams() > 0);
    }

    // ---- the payoff: a self-weighing bill of materials ----

    /// <summary>
    /// An ISO 7089 M6 washer is an exact annulus, so its mass has a closed form:
    /// (pi/4)(12^2 - 6.4^2) x 1.6 mm^3 x 7850 kg/m^3 = 1.01645 g. Nothing but the
    /// component's own table, the density above and <c>BrepMassProperties</c> feeds it.
    /// </summary>
    [Fact]
    public void AWashersMassIsTheAnalyticAnnulus()
    {
        var washer = StandardComponents.Washer(6);
        double volume = Math.PI / 4 * (12.0 * 12.0 - 6.4 * 6.4) * 1.6;
        double expected = ModelUnits.MassToGrams(volume * FastenerMaterials.CarbonSteel.Density);

        double actual = washer.ToPart().MassGrams()!.Value;
        Assert.Equal(1.01645, expected, 5);
        // Richardson-extrapolated tessellate-then-sum: ~1e-7 relative on curved solids.
        Assert.Equal(expected, actual, expected * 1e-5);
    }

    /// <summary>
    /// The user-visible payoff end to end: place hardware, ask for the bill of materials,
    /// and every bought-in line already knows its mass. Nothing in the design says what a
    /// screw is made of.
    /// </summary>
    [Fact]
    public void ABomOfBoughtInPartsWeighsItself()
    {
        var build = new ComponentAssembly("plate", Shape.Box(60, 40, 8));
        var top = SketchPlane.At((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        build.Place(StandardComponents.CapScrew(6, 20), [new(-20, 0), new(20, 0)], top);
        build.Place(StandardComponents.Washer(6), [new(-20, 0), new(20, 0)], top);
        var assembly = build.ToAssembly();
        build.Host!.Material = Materials.Aluminium6061;

        var bom = Bom.For(assembly);
        Assert.True(bom.HasMaterials);

        foreach (var line in bom.Hardware)
        {
            Assert.NotNull(line.Material);
            Assert.NotNull(line.UnitMassGrams);
            Assert.Equal(2, line.Quantity);
            Assert.Equal(line.UnitMassGrams!.Value * 2, line.TotalMassGrams!.Value, 1e-9);
        }

        // Two washers of the analytic mass above, and the total covers every line.
        var washers = bom.Lines.Single(l => l.Item.StartsWith("ISO 7089", StringComparison.Ordinal));
        Assert.Equal(2 * 1.01645, washers.TotalMassGrams!.Value, 1e-4);

        string text = bom.ToText(mass: true);
        Assert.Contains("Alloy steel", text);
        Assert.Contains("Carbon steel", text);
        // Every line states a material, so the footer's total needs no "over the N of M"
        // qualifier - which is the honest signal that nothing was silently skipped.
        Assert.DoesNotContain("over the", text);
    }

    /// <summary>
    /// A catalogue material survives a document round trip, so a reopened model still
    /// weighs its hardware. It travels by VALUE (name plus the properties actually
    /// stated), not as a lookup into a catalogue, which is what makes a
    /// <see cref="FastenerMaterials"/> grade come back even though
    /// <see cref="Materials.All"/> does not carry it.
    /// </summary>
    [Fact]
    public void ACatalogueMaterialSurvivesADocumentRoundTrip()
    {
        var scene = new Scene();
        scene.Add(StandardComponents.CapScrew(6, 20).ToPart());
        scene.Add(StandardComponents.TrisertInsert(6).ToPart());

        var loaded = Document.Load(new Document(scene).Save()).Scene;

        Assert.Equal(FastenerMaterials.AlloySteel, loaded.Tabs[0].Parts[0].Material);
        Assert.Equal(FastenerMaterials.Brass, loaded.Tabs[0].Parts[1].Material);
    }

    /// <summary>
    /// A brass insert really does weigh more than a steel one of the same size: the mass
    /// difference comes from the density and nothing else, so the ratio is the densities'.
    /// This is what makes stating the material worth doing rather than decorative.
    /// </summary>
    [Fact]
    public void BrassAndSteelOfOneShapeDifferByTheirDensities()
    {
        var insert = StandardComponents.TrisertInsert(6);
        double brass = insert.ToPart().MassGrams()!.Value;

        var steelTwin = new Part("twin", insert.Body).Of(FastenerMaterials.CarbonSteel);
        double steel = steelTwin.MassGrams()!.Value;

        double expected = FastenerMaterials.Brass.Density / FastenerMaterials.CarbonSteel.Density;
        Assert.Equal(expected, brass / steel, 1e-9);
        Assert.True(brass > steel);
    }
}
