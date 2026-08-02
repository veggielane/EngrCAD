using EngrCAD.Core;
using EngrCAD.Implicit;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The helical pair's conjugate action, established the way the transverse-section
/// argument says it should be.
/// </summary>
/// <remarks>
/// <para>Conjugate action for a SPUR pair is already measured from contact in
/// <c>GearTests</c> — deliberately not through <c>Coupling.Gear</c>, which enforces the
/// ratio it would be asserting. A helical pair adds axial overlap and nothing else: at
/// every transverse section the pair IS a spur pair, because a helical gear's section at
/// height z is its own spur profile rotated by ψ(z) and a meshing pair's two members are
/// rotated by +ψ(z) and −ψ(z) (opposite hands on parallel axes). Rotating both members
/// rigidly changes the PHASE of contact and nothing about the ratio, so conjugacy at one
/// section carries to all of them.</para>
/// <para>What that argument leaves open — and therefore what these tests measure — is
/// whether the built solid's sections really ARE the spur profile rotated by ψ(z). That
/// is the half a twist law, a scaling slip or a subdivision artifact could break, and it
/// is checked as a distance from real transverse sections of the mesh to the exact spur
/// region's zero level, with the bound derived from the three error sources rather than
/// chosen.</para>
/// </remarks>
public class HelicalSectionTests
{
    [Fact]
    public void HelicalSections_AreTheSpurProfileRotatedByTheTwistLaw()
    {
        var spec = new GearSpec(module: 2, teeth: 20);
        const double width = 20, beta = 20;
        const int slices = 64;
        double r = spec.PitchDiameter / 2;
        var quality = new MeshQuality { SegmentsPerCircle = 256 };
        var spur = Gears.Spur(spec).Sketch.ToRegion();
        var shape = Gears.HelicalGear(spec, width, beta, slices: slices);

        foreach (double z in new[] { 2.7, 9.1, 17.3 })
        {
            double psi = z * Math.Tan(beta * Math.PI / 180) / r;
            double worst = SectionDeviation(shape, quality, spur, z, psi);
            Assert.True(worst <= SectionBound(spec, width, beta, slices, quality),
                $"at z = {z} the section deviates {worst:0.###e0} from the spur profile "
                + $"rotated by {psi:0.#####} rad");
        }
    }

    [Fact]
    public void SectionInstrument_SeesAFivePercentWrongTwist()
    {
        // The mutation check: measured against a rotation 5% off the twist law, the same
        // sections must be visibly wrong, or the bound above is measuring nothing.
        var spec = new GearSpec(module: 2, teeth: 20);
        const double width = 20, beta = 20;
        double r = spec.PitchDiameter / 2;
        var quality = new MeshQuality { SegmentsPerCircle = 256 };
        var spur = Gears.Spur(spec).Sketch.ToRegion();
        var shape = Gears.HelicalGear(spec, width, beta, slices: 64);

        const double z = 17.3;
        double psi = z * Math.Tan(beta * Math.PI / 180) / r;
        double wrong = SectionDeviation(shape, quality, spur, z, psi * 1.05);
        Assert.True(wrong > 100 * SectionBound(spec, width, beta, 64, quality),
            $"a 5% twist error still reads {wrong:0.###e0}");
    }

    [Fact]
    public void HerringboneSections_FollowTheirOwnLaw_InBothHalves()
    {
        // The same instrument applied to the double-helical form, at matched distances
        // either side of the apex: both halves reproduce the spur profile rotated by the
        // SAME angle, which is the transverse-section reading of the mirror identity —
        // and is what makes the herringbone a conjugate pair in both halves at once.
        var spec = new GearSpec(module: 2, teeth: 20);
        const double width = 20, beta = 20;
        const int slicesPerHalf = 48;
        var quality = new MeshQuality { SegmentsPerCircle = 256 };
        var spur = Gears.Spur(spec).Sketch.ToRegion();
        var shape = HerringboneGears.Herringbone(
            spec, width, beta, slicesPerHalf: slicesPerHalf, quality: quality);

        double bound = SectionBound(spec, width / 2, beta, slicesPerHalf, quality);
        foreach (double z in new[] { 2.7, 7.1, width - 7.1, width - 2.7 })
        {
            double psi = HerringboneGears.SectionAngleAt(spec, width, beta, z);
            double worst = SectionDeviation(shape, quality, spur, z, psi);
            Assert.True(worst <= bound,
                $"at z = {z} the section deviates {worst:0.###e0} from the spur profile "
                + $"rotated by {psi:0.#####} rad (bound {bound:0.###e0})");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The largest distance from any boundary point of the solid's transverse section at
    /// <paramref name="z"/> to the spur region's boundary, after rotating the section
    /// back by <paramref name="psi"/>. Zero would mean the section IS the spur profile
    /// rotated by exactly that angle.
    /// </summary>
    private static double SectionDeviation(
        Shape shape, MeshQuality quality, IPlanarRegion spur, double z, double psi)
    {
        var plane = SketchPlane.At(new Vector3d(0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
        var regions = shape.Section(plane, quality: quality);
        Assert.NotEmpty(regions);

        double cos = Math.Cos(-psi), sin = Math.Sin(-psi);
        double worst = 0;
        foreach (var region in regions)
        {
            foreach (var loop in region.AllLoops())
            {
                foreach (var p in loop)
                {
                    var q = new Vector2d(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
                    worst = Math.Max(worst, Math.Abs(spur.SignedDistance(q)));
                }
            }
        }
        return worst;
    }

    /// <summary>
    /// The three error sources a sectioned twisted mesh carries, summed rather than
    /// combined in quadrature (they are systematic, not random): the sketch's ARC
    /// FLATTENING, whose sagitta is what the sweep's own chord tolerance allows at the
    /// profile's outer radius; the WALL PANEL chord, since a section between two rings
    /// reads a straight interpolation of a point that truly rotates by the per-slab
    /// angle; and the involute's own BIARC FIT deviation. Nothing here is tuned — each
    /// term is the closed form for its own approximation.
    /// </summary>
    private static double SectionBound(
        GearSpec spec, double twistedHeight, double helixAngleDegrees, int slices, MeshQuality quality)
    {
        double outer = spec.TipDiameter / 2;
        double flattening = outer * (1 - Math.Cos(Math.PI / Math.Max(8, quality.SegmentsPerCircle)));
        double perSlab = HelicalGearGeometry.Twist(spec.PitchDiameter / 2, twistedHeight, helixAngleDegrees)
            / slices;
        double panel = outer * (1 - Math.Cos(Math.Abs(perSlab) / 2));
        double fit = spec.Module * 1e-4;
        return flattening + panel + fit;
    }
}
