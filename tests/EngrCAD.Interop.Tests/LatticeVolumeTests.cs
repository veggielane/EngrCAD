using EngrCAD.Core;
using EngrCAD.Implicit;
using Xunit;
using Xunit.Abstractions;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The lattice families' volume fractions, cross-checked by POLYGONIZATION.
/// <para>
/// This is a second instrument rather than a second run of the first. The implicit engine
/// answers "what fraction of space is material" by counting samples of the field over one
/// cell; here the same solid is meshed by Surface Nets and its volume integrated over the
/// triangles. The two share nothing but the field, so agreement is two measurements checking
/// each other — which is exactly what the 50/50 claim at a TPMS's symmetric level needs,
/// since a sampled count and a closed form agreeing at 0.5 would otherwise both be resting on
/// the same grid.
/// </para>
/// </summary>
public class LatticeVolumeTests(ITestOutputHelper output)
{
    /// <summary>
    /// The known answer: at level 0 the four antisymmetric surfaces split space exactly in
    /// half, so the network inside a block of whole cells fills half of it. Measured by mesh
    /// volume, with the block cut well inside the polygonized region so no boundary effect
    /// enters.
    /// </summary>
    [Theory]
    [InlineData(TpmsKind.SchwarzP)]
    [InlineData(TpmsKind.SchwarzD)]
    [InlineData(TpmsKind.Gyroid)]
    [InlineData(TpmsKind.FischerKochS)]
    public void ANetworkAtLevelZero_PolygonizesToHalfTheBlock(TpmsKind kind)
    {
        const double Cell = 10, Half = Cell;
        var block = Sdf.Box(2 * Half, 2 * Half, 2 * Half);
        var solid = Sdf.TpmsSolid(kind, Cell) & block;

        var mesh = SurfaceNets.Polygonize(solid, resolution: 150);
        double fraction = mesh.Volume() / Math.Pow(2 * Half, 3);
        output.WriteLine($"{kind,-14} polygonized fraction {fraction:0.####}");

        // Surface Nets is an inscribed approximation of a surface with a great deal of area
        // per unit volume here, so a couple of percent is the grid's own resolution rather
        // than a disagreement about the geometry.
        Assert.Equal(0.5, fraction, 0.02);
    }

    /// <summary>
    /// The sheet's thickness is a LOWER bound on the wall, which is the family's defining
    /// caveat: the field divides by the GLOBAL maximum of |grad F| while the local gradient on
    /// the surface is smaller, so the wall comes out thicker by that ratio. Measured here as
    /// volume rather than asserted in prose — the sheet's material must EXCEED the nominal
    /// surface-area-times-thickness and must not exceed it by more than the recorded worst
    /// factor.
    /// </summary>
    [Fact]
    public void ASheetIsAtLeastAsThickAsAsked_AndTheExcessIsBounded()
    {
        const double Cell = 10, Thickness = 1.2, Half = 10;
        var block = Sdf.Box(2 * Half, 2 * Half, 2 * Half);
        var sheet = Sdf.TpmsSheet(TpmsKind.Gyroid, Cell, Thickness) & block;

        double volume = SurfaceNets.Polygonize(sheet, resolution: 150).Volume();
        // The gyroid's area is 3.091 cell^2 per cell (Schoen); a wall of exactly the nominal
        // thickness would therefore hold this much.
        double cells = Math.Pow(2 * Half / Cell, 3);
        double nominal = 3.091 * Cell * Cell * cells * Thickness;
        double factor = volume / nominal;

        output.WriteLine($"gyroid sheet: {volume:0.#} against a nominal {nominal:0.#} — factor {factor:0.###}");
        Assert.True(factor > 1.0, $"the wall came out THINNER than asked (factor {factor:R})");
        // The recorded worst thickness factor for the gyroid is 1.22; the mesh is inscribed,
        // so the measurement sits a little under it.
        Assert.True(factor < 1.25, $"the wall is {factor:R} times the nominal thickness");
    }

    /// <summary>
    /// The strut lattice's exactness claim, cross-checked the same way: the diameter means
    /// what it says, so the polygonized volume must agree with the implicit engine's own
    /// sampled fraction — two estimators, one geometry.
    /// </summary>
    [Theory]
    [InlineData(StrutLatticeKind.SimpleCubic)]
    [InlineData(StrutLatticeKind.BodyCentredCubic)]
    [InlineData(StrutLatticeKind.Octet)]
    [InlineData(StrutLatticeKind.Diamond)]
    public void AStrutLatticePolygonizesToItsMeasuredVolumeFraction(StrutLatticeKind kind)
    {
        const double Cell = 10, Half = 10, Diameter = 2.4;
        var block = Sdf.Box(2 * Half, 2 * Half, 2 * Half);
        var lattice = Sdf.StrutLattice(kind, Cell, Diameter) & block;

        double meshed = SurfaceNets.Polygonize(lattice, resolution: 150).Volume() / Math.Pow(2 * Half, 3);
        double sampled = StrutLattices.VolumeFraction(kind, Cell, Diameter);
        output.WriteLine($"{kind,-18} polygonized {meshed:0.####} against sampled {sampled:0.####}");

        Assert.Equal(sampled, meshed, 0.02);
    }

    /// <summary>
    /// The whole point of asking for a volume fraction: it lands. The fit solves a diameter
    /// from one sampled grid, the engine reports the fraction from another, and the mesh is a
    /// third reading of the same solid.
    /// </summary>
    [Fact]
    public void AVolumeFractionFit_PolygonizesToWhatItPromised()
    {
        const double Cell = 10, Half = 10;
        var fit = StrutLattices.ForVolumeFraction(StrutLatticeKind.Octet, Cell, 0.25);
        var block = Sdf.Box(2 * Half, 2 * Half, 2 * Half);

        double meshed = SurfaceNets.Polygonize(fit.Field & block, resolution: 150).Volume()
            / Math.Pow(2 * Half, 3);
        output.WriteLine($"octet at diameter {fit.Parameter:0.###}: reported {fit.VolumeFraction:0.####}, " +
                         $"polygonized {meshed:0.####}");

        Assert.Equal(fit.VolumeFraction, meshed, 0.02);
    }
}
