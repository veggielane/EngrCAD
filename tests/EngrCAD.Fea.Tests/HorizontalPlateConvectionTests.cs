using Xunit;

namespace EngrCAD.Fea.Tests;

/// <summary>
/// The McAdams horizontal-plate family: constants asserted in the datasheet's own form,
/// the facing-up/facing-down factor of exactly two, and the identity with teeth — the
/// turbulent branch's 1/3 power cancels the L-cubed in Ra, so the film coefficient is
/// SIZE-independent, which a transcription error in either the exponent or the constant
/// cannot pass.
/// </summary>
public class HorizontalPlateConvectionTests
{
    [Fact]
    public void TheConstants_AreTheDatasheetForm()
    {
        // The transcription asserted as the reference states it (never re-derived).
        Assert.Equal(0.54 * Math.Pow(2e6, 0.25),
            NaturalConvection.PlateNusselt(2e6, NaturalConvection.PlateFacing.HeatedFacingUp));
        Assert.Equal(0.27 * Math.Pow(2e6, 0.25),
            NaturalConvection.PlateNusselt(2e6, NaturalConvection.PlateFacing.HeatedFacingDown));
        Assert.Equal(0.15 * Math.Pow(4e8, 1.0 / 3),
            NaturalConvection.PlateNusselt(4e8, NaturalConvection.PlateFacing.HeatedFacingUp));
        // A square plate's characteristic length is a quarter of its side (A/P).
        Assert.Equal(0.025, NaturalConvection.PlateCharacteristicLength(0.01, 0.4), 15);
    }

    [Fact]
    public void FacingUp_IsExactlyTwiceFacingDown_InTheSharedLaminarRange()
    {
        // 0.54 = 2 x 0.27 and doubling commutes with rounding, so the ratio is exact to
        // the bit at every Ra where both quarter-power branches are valid (1e5..1e7).
        foreach (double ra in new[] { 1e5, 7.7e5, 4.2e6, 1e7 })
            Assert.Equal(
                2 * NaturalConvection.PlateNusselt(ra, NaturalConvection.PlateFacing.HeatedFacingDown),
                NaturalConvection.PlateNusselt(ra, NaturalConvection.PlateFacing.HeatedFacingUp));
    }

    [Fact]
    public void TheTurbulentFilmCoefficient_IsIndependentOfThePlateSize()
    {
        // h = 0.15 k (g beta dT / (nu alpha))^(1/3): Ra carries L^3 and the 1/3 power
        // removes it. A 1x1 m and a 2x2 m plate at the same 50 K rise must read ONE
        // film coefficient — the identity a wrong exponent cannot fake.
        var up = NaturalConvection.PlateFacing.HeatedFacingUp;
        double small = NaturalConvection.PlateFilmCoefficient(50, 1.0, 4.0, up);
        double large = NaturalConvection.PlateFilmCoefficient(50, 4.0, 8.0, up);
        Assert.Equal(small, large, small * 1e-12);
        // And the number lands in the handbook's few-W/(m2 K) natural-convection band.
        Assert.InRange(small, 2.0, 10.0);
    }

    [Fact]
    public void OutsideTheValidityRange_RefusesByName()
    {
        var up = NaturalConvection.PlateFacing.HeatedFacingUp;
        var down = NaturalConvection.PlateFacing.HeatedFacingDown;
        Assert.Contains("validity", Assert.Throws<ArgumentException>(() =>
            NaturalConvection.PlateNusselt(1e3, up)).Message);
        Assert.Contains("10^5", Assert.Throws<ArgumentException>(() =>
            NaturalConvection.PlateNusselt(5e4, down)).Message);
        Assert.Contains("validity", Assert.Throws<ArgumentException>(() =>
            NaturalConvection.PlateNusselt(1e11, down)).Message);
    }
}
