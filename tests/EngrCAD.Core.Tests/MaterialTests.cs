using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The unified <see cref="Material"/> and the one unit convention behind it.
///
/// <para><b>The point of these tests is the UNIT, not the plumbing.</b> Before the type moved
/// here, two densities were documented a factor of 1000 apart — the simulation catalogue's
/// tonne/mm³ and the document model's kg/mm³ — and nothing could catch a caller taking one
/// for the other, because neither figure is wrong on its own. The tests below pin the
/// catalogue against the DATASHEET figure in kg/m³, which is the only number a human can
/// check, and pin the conversion that gets there.</para>
/// </summary>
public class MaterialTests
{
    [Fact]
    public void DocumentMaterial_NeedsNothingButANameAndADensity()
    {
        // The whole reason Material moved out of EngrCAD.Fea: its constructor refused
        // youngsModulus <= 0, so a bill-of-materials entry was not constructible through it.
        var delrin = new Material("Delrin", density: 1.41e-9);

        Assert.Equal("Delrin", delrin.Name);
        Assert.Equal(1.41e-9, delrin.Density);
        Assert.False(delrin.HasElasticity);
        Assert.Equal(0, delrin.YoungsModulus);
        Assert.Equal(0, delrin.Lambda);
        Assert.Equal(0, delrin.Mu);
    }

    [Fact]
    public void AnalysisProperties_AreStillCheckedWhenTheyAreStated()
    {
        // Optional does not mean unvalidated: a negative modulus and an incompressible
        // Poisson's ratio are still refused where they are given.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Material("bad", youngsModulus: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Material("bad", 210_000, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Material("bad", density: -1));
        Assert.Throws<ArgumentException>(() => new Material(" "));
    }

    [Fact]
    public void HasElasticity_ReadsTheModulusAlone()
    {
        // Poisson's ratio has a legal value of zero, so it cannot distinguish "not stated"
        // from "stated as zero" and does not take part in the test. A solid with no
        // stiffness at all, by contrast, is not a material.
        Assert.True(new Material("cork-ish", youngsModulus: 20, poissonsRatio: 0).HasElasticity);
        Assert.False(new Material("unstated", poissonsRatio: 0.3).HasElasticity);
    }

    /// <summary>
    /// <b>The unit decision, pinned against the only checkable form of each figure.</b> The
    /// catalogue stores tonne/mm³ because that is what an equation consumes; the assertion
    /// is in kg/m³ because that is what a datasheet prints.
    /// </summary>
    [Theory]
    [InlineData("Structural steel", 7850.0)]
    [InlineData("Stainless steel 304", 8000.0)]
    [InlineData("Aluminium 6061-T6", 2700.0)]
    [InlineData("Aluminium 7075-T6", 2810.0)]
    [InlineData("Titanium Ti-6Al-4V", 4430.0)]
    [InlineData("Grey cast iron", 7200.0)]
    [InlineData("Brass C36000", 8500.0)]
    [InlineData("ABS", 1040.0)]
    [InlineData("PLA", 1250.0)]
    [InlineData("Nylon 6/6", 1140.0)]
    public void Catalogue_DensitiesAreTheDatasheetFiguresInTonnePerCubicMillimetre(
        string name, double kilogramsPerCubicMetre)
    {
        var material = Materials.All.Single(m => m.Name == name);

        // Relative, because 7.85e-9 x 1e12 is not bit-exactly 7850.
        Assert.Equal(kilogramsPerCubicMetre, material.DensityKilogramsPerCubicMetre, 9);
        Assert.Equal(
            ModelUnits.DensityFromKilogramsPerCubicMetre(kilogramsPerCubicMetre),
            material.Density,
            15);

        // And the figure a caller who typed the SI number by mistake would have used is a
        // thousand times the one stored -- which is exactly the discrepancy this settled.
        Assert.Equal(1000.0, (kilogramsPerCubicMetre * 1e-9) / material.Density, 9);
    }

    [Fact]
    public void DensityConversions_RoundTrip()
    {
        double density = ModelUnits.DensityFromKilogramsPerCubicMetre(7850);
        Assert.Equal(7.85e-9, density, 15);
        Assert.Equal(7850, ModelUnits.DensityToKilogramsPerCubicMetre(density), 9);
    }

    [Fact]
    public void MassConversions_FollowFromTheDensityUnit()
    {
        // A 100 x 20 x 5 mm aluminium plate: 10 000 mm3 at 2.7e-9 t/mm3.
        double mass = 10_000 * Materials.Aluminium6061.Density;   // tonnes
        Assert.Equal(2.7e-5, mass, 15);
        Assert.Equal(0.027, ModelUnits.MassToKilograms(mass), 12);
        Assert.Equal(27.0, ModelUnits.MassToGrams(mass), 9);
        Assert.Equal(mass, ModelUnits.MassFromKilograms(0.027), 15);
    }

    [Fact]
    public void CatalogueEntriesCarryNoColor_SoAssigningOneMovesNoPixels()
    {
        // Appearance is a finish, not a property of the stuff. If a catalogue entry carried
        // a color, assigning it to a part would override the palette and change a render.
        Assert.All(Materials.All, m => Assert.Null(m.Color));
    }

    [Fact]
    public void WithCalls_CarryEveryOtherPropertyOver()
    {
        var painted = Materials.Steel
            .WithColor(new PartColor(0.9f, 0.2f, 0.2f))
            .WithDensity(7.9e-9)
            .WithName("Painted steel");

        Assert.Equal("Painted steel", painted.Name);
        Assert.Equal(7.9e-9, painted.Density);
        Assert.Equal(new PartColor(0.9f, 0.2f, 0.2f), painted.Color);
        // Everything not named is carried, which a record's generated `with` cannot do for
        // get-only properties behind a validating constructor.
        Assert.Equal(Materials.Steel.YoungsModulus, painted.YoungsModulus);
        Assert.Equal(Materials.Steel.ThermalConductivity, painted.ThermalConductivity);
        Assert.Equal(Materials.Steel.SpecificHeat, painted.SpecificHeat);
        Assert.Equal(Materials.Steel.ThermalExpansion, painted.ThermalExpansion);
    }

    [Fact]
    public void WithElasticity_TurnsADocumentMaterialIntoAnAnalysisOne()
    {
        var document = new Material("Some alloy", density: 4.5e-9);
        Assert.False(document.HasElasticity);

        var analysis = document.WithElasticity(110_000, 0.34);
        Assert.True(analysis.HasElasticity);
        Assert.Equal(4.5e-9, analysis.Density);
        Assert.Equal(110_000 / (2 * 1.34), analysis.Mu, 9);
    }

    [Fact]
    public void GravityIsOneVector_UnderBothNames()
    {
        Assert.Equal(ModelUnits.Gravity, Materials.GravityMillimetres);
        Assert.Equal(-9806.65, Materials.GravityMillimetres.Z);
        Assert.Equal(-9.80665, Materials.GravityMetres.Z);
    }

    [Fact]
    public void DerivedThermalNumbers_AreTheDocumentedOnes()
    {
        // Steel: rho.c = 7.85e-9 x 4.60e8 = 3.611 mJ/(mm3.K), diffusivity 50/3.611 = 13.85 mm2/s
        // (the SI 1.385e-5 m2/s). Both figures appear in the doc comment; pin them.
        Assert.Equal(3.611, Materials.Steel.VolumetricHeatCapacity, 9);
        Assert.Equal(13.8466, Materials.Steel.ThermalDiffusivity, 4);
        // 167 / (2.70e-9 x 8.96e8) = 69.03. The doc comment said 68.7 until this assertion
        // was written -- a quoted figure rather than one computed from the catalogue's own
        // rows, which is exactly the kind of drift a derived property should be pinned for.
        Assert.Equal(69.031, Materials.Aluminium6061.ThermalDiffusivity, 3);
        // No capacity stated => no transient; an exact-zero semantic case, not a small number.
        Assert.Equal(0, new Material("x", thermalConductivity: 50).ThermalDiffusivity);
    }
}
