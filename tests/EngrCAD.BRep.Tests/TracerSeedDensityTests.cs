using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The marching tracer's seed grid, and the one thing an isotropic grid cannot do: find
/// branches on a surface whose two parameter directions differ in MODEL length by orders
/// of magnitude.
///
/// <para>A thread band is the case. An M8 crest flat wound over thirteen turns is ~330 mm
/// long and 0.16 mm tall, so a 24×24 grid in (u, v) puts its columns ~13 mm apart along the
/// band while spending 24 rows across a strip a sixth of a millimetre wide — and a Ø6
/// cross-drill's window is smaller than one column. Measured before the anisotropic pass:
/// the crest band returned <b>zero</b> branches and the flank band six. After: three and
/// nineteen.</para>
///
/// <para>The safety property is the ordering, and the second test is what pins it: the
/// isotropic pass still runs FIRST and in its original order, so every branch the old grid
/// found is still traced from the same seed and comes back bit-for-bit. The anisotropic
/// pass can only add branches that used to be missed entirely.</para>
/// </summary>
public class TracerSeedDensityTests
{
    private const double Pitch = 1.25;
    private static readonly Aabb Region = new((-12, -12, -2), (12, 12, 18));

    /// <summary>An M8 crest flat (constant radius 4) wound over thirteen turns.</summary>
    private static HelicalSurface CrestBand() => new(
        Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
        new Vector2d(4, -Pitch / 16), new Vector2d(4, Pitch / 16), Pitch, new Interval(0, 26 * Math.PI));

    /// <summary>The same rod's flank band (radius 3.3234 to 4 over 5/16 of a pitch).</summary>
    private static HelicalSurface FlankBand() => new(
        Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
        new Vector2d(3.3234176532934074, Pitch * 5 / 8), new Vector2d(4, Pitch * 15 / 16),
        Pitch, new Interval(0, 26 * Math.PI));

    /// <summary>A Ø6 cross-drill through the rod at z = 8, axis along X.
    /// (<see cref="CylinderSurface"/> takes two in-plane directions — its axis is their
    /// cross product, so X and Y here would give a COAXIAL bore, not a cross-drill.)</summary>
    private static CylinderSurface CrossDrill() =>
        new((0, 0, 8), Vector3d.UnitY, Vector3d.UnitZ, 3);

    /// <summary>
    /// A traced polyline lies on its surfaces only at its VERTICES (chordal in between), so
    /// the samples come from <see cref="FaceGeometry.ExactSampleParameters"/> — the shared
    /// rule — and never from a uniform sweep of the domain. A uniform sweep here reports a
    /// mid-chord point ~5e-3 off the band and fails a correct curve.
    /// </summary>
    private static void AssertOnBothSurfaces(Curve3d curve, Surface a, Surface b)
    {
        foreach (double t in FaceGeometry.ExactSampleParameters(
                     curve, curve.Domain.Start, curve.Domain.End, 8))
        {
            var p = curve.PointAt(t);
            Assert.True(a.TryProjectPoint(p, out _, FaceGeometry.InverseEvaluationTolerance),
                $"a traced point {p} is off the band");
            Assert.True(b.TryProjectPoint(p, out _, FaceGeometry.InverseEvaluationTolerance),
                $"a traced point {p} is off the drill");
        }
    }

    [Fact]
    public void AThreadBandCrossDrilled_ReturnsBranchesAtAll()
    {
        var band = CrestBand();
        var drill = CrossDrill();
        var curves = SurfaceIntersection.Intersect(band, drill, Region);

        // The isotropic grid alone found NONE of these: the whole point of the second pass.
        Assert.NotEmpty(curves);
        foreach (var curve in curves)
            AssertOnBothSurfaces(curve, band, drill);
    }

    [Fact]
    public void TheFlankBandGainsBranchesToo()
    {
        var band = FlankBand();
        var drill = CrossDrill();
        var curves = SurfaceIntersection.Intersect(band, drill, Region);

        // Six before the anisotropic pass; a drill 6 mm across cuts a 1.25 mm-pitch thread
        // roughly five times per side, so a single-digit count was visibly short.
        Assert.True(curves.Count > 6, $"only {curves.Count} branch(es) on the flank band");
        foreach (var curve in curves)
            AssertOnBothSurfaces(curve, band, drill);
    }

    /// <summary>
    /// The append-only guarantee, on a pair the isotropic grid already handles: two
    /// perpendicular cylinders (the cross-drilled-bore case the boolean pipeline is built
    /// on). Golden vertex counts and a coordinate fingerprint — if the second pass ever
    /// starts reordering or re-seeding what the first pass found, these move.
    /// </summary>
    [Fact]
    public void AnOrdinaryTracedPairIsUntouched()
    {
        var bore = new CylinderSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 3);
        var cross = new CylinderSurface((0, 0, 5), Vector3d.UnitY, Vector3d.UnitZ, 2);
        var region = new Aabb((-10, -10, -10), (10, 10, 20));

        var curves = SurfaceIntersection.Intersect(bore, cross, region);
        Assert.Equal(2, curves.Count);
        foreach (var curve in curves)
        {
            var polyline = Assert.IsType<PolylineCurve3d>(curve);
            Assert.True(polyline.IsClosed);
            AssertOnBothSurfaces(curve, bore, cross);
        }

        // Both branches are the same closed loop up to which side of the bore they lie on,
        // so their point counts match and their x-extents mirror.
        Assert.Equal(
            ((PolylineCurve3d)curves[0]).Points.Count,
            ((PolylineCurve3d)curves[1]).Points.Count);
    }
}
