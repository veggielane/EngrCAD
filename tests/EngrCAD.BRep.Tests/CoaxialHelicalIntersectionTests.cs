using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The exact intersection of a helical band with a COAXIAL surface of revolution whose
/// (radius, axial) profile is a straight line — a thread's 45-degree end-chamfer cone, a
/// coaxial cylinder, a coaxial disk.
/// <para>These pairs used to fall to the marching tracer, whose output is chordal and
/// whose sample count is fixed at boolean time. They do not need it: substituting the
/// band's r = r0 + dr·v, z = z0 + dz·v + rate·u into the carrier's r = a + b·z gives
/// v·(dr − b·dz) = (a + b·z0 − r0) + b·rate·u, so <b>v is LINEAR in u</b> and the curve
/// is a conical spiral in the band's own frame. The cap cuts <c>MakeThreadedRod</c>
/// already builds are the b-in-z = 0 member of the same family, which is why one
/// <see cref="SpiralArc3d"/> spells them all.</para>
/// </summary>
public class CoaxialHelicalIntersectionTests
{
    private const double Pitch = 1.25;
    private static readonly Aabb Region = new((-20, -20, -20), (20, 20, 20));

    /// <summary>One flank band of an M8-like thread: radius 3.3 to 4 over 0.4 of axial
    /// rise, wound over three turns.</summary>
    private static HelicalSurface Flank() => new(
        Frame3d.FromOrthonormal(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
        new Vector2d(3.3, 0), new Vector2d(4, 0.4), Pitch, new Interval(0, 6 * Math.PI));

    /// <summary>A coaxial full revolve of the straight generator from (r0, z0) to (r1, z1)
    /// in the axial half-plane — the shape of every chamfer cone.</summary>
    private static RevolvedSurface Coaxial(double r0, double z0, double r1, double z1) => new(
        new Line3d((r0, 0, z0), (r1, 0, z1)), Vector3d.Zero, Vector3d.UnitZ);

    /// <summary>Largest distance from a curve sample to each carrier, measured in the
    /// carriers' own closed forms rather than by projection — a projection onto a bounded
    /// generator clamps at its ends and would report a residual that is not there.</summary>
    private static (double OnBand, double OnCarrier) Residuals(
        Curve3d curve, HelicalSurface band, double a, double b)
    {
        double onBand = 0, onCarrier = 0;
        for (int i = 0; i <= 128; i++)
        {
            var p = curve.PointAt(curve.Domain.ParameterAt(i / 128.0));
            Assert.True(band.TryProjectPoint(p, out var uv, 1e-6));
            onBand = Math.Max(onBand, band.PointAt(uv.X, uv.Y).DistanceTo(p));
            onCarrier = Math.Max(onCarrier, Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - (a + b * p.Z)));
        }
        return (onBand, onCarrier);
    }

    [Fact]
    public void AChamferConeCutsAHelicalBandInAnExactConicalSpiral()
    {
        // r = 3.3 + (2 - z): a 45-degree cone whose radius reaches the band's root at
        // z = 2, exactly the geometry of a thread's end chamfer.
        var band = Flank();
        var cone = Coaxial(3.3, 2, 5.3, 0);
        double a = 3.3 + 2, b = -1;

        var curves = SurfaceIntersection.Intersect(band, cone, Region);
        var spiral = Assert.IsType<SpiralArc3d>(Assert.Single(curves));
        Assert.False(spiral.IsPlanar);        // a cone cut advances axially
        Assert.NotEqual(0, spiral.Slope);     // and radially

        var (onBand, onCarrier) = Residuals(spiral, band, a, b);
        Assert.True(onBand < 1e-12, $"off the band by {onBand:E3}");
        Assert.True(onCarrier < 1e-12, $"off the cone by {onCarrier:E3}");

        // The span is exactly where v runs 0 to 1: the ends sit ON the rails.
        foreach (double t in (double[])[spiral.Domain.Start, spiral.Domain.End])
        {
            Assert.True(band.TryProjectPoint(spiral.PointAt(t), out var uv, 1e-9));
            Assert.True(Math.Abs(uv.Y) < 1e-9 || Math.Abs(uv.Y - 1) < 1e-9,
                $"a cut end sits at v = {uv.Y:F12}, which is neither rail");
        }
    }

    [Fact]
    public void ACoaxialCylinderCutsAHelicalBandInAnExactHelix()
    {
        // b = 0, so v is CONSTANT: the cut is one complete iso-v helix over the band's
        // whole domain — a turned undercut or a thread runout diameter.
        var band = Flank();
        var cylinder = new CylinderSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 3.65);

        var curves = SurfaceIntersection.Intersect(band, cylinder, Region);
        var spiral = Assert.IsType<SpiralArc3d>(Assert.Single(curves));
        Assert.Equal(0, spiral.Slope, 12);                  // constant radius
        Assert.Equal(Pitch / (2 * Math.PI), spiral.AxialRate, 12);   // pure helix
        Assert.Equal(band.DomainU.Start, spiral.Domain.Start, 12);
        Assert.Equal(band.DomainU.End, spiral.Domain.End, 12);

        var (onBand, onCarrier) = Residuals(spiral, band, 3.65, 0);
        Assert.True(onBand < 1e-12, $"off the band by {onBand:E3}");
        Assert.True(onCarrier < 1e-12, $"off the cylinder by {onCarrier:E3}");
    }

    [Fact]
    public void ACoaxialCylinderClearOfTheBandReportsNothing()
    {
        var band = Flank();
        Assert.Empty(SurfaceIntersection.Intersect(
            band, new CylinderSurface(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 4.5), Region));
    }

    [Fact]
    public void AConeParallelToTheGeneratorReportsNothing()
    {
        // dr = b·dz makes the substitution's denominator vanish: the carrier's profile is
        // parallel to the band's, so they never cross transversally. A tangential contact
        // is not reported here by contract, so this is empty rather than a refusal.
        var band = Flank();            // dr = 0.7, dz = 0.4, so b = 1.75 is parallel
        var cone = Coaxial(3.3, 0, 3.3 + 1.75 * 2, 2);
        Assert.Empty(SurfaceIntersection.Intersect(band, cone, Region));
    }

    [Fact]
    public void AConeWhoseGeneratorStopsShortClipsTheCut()
    {
        // The carrier's own axial extent bounds the cut: half the generator, half the span.
        var band = Flank();
        var full = SurfaceIntersection.Intersect(band, Coaxial(3.3, 2, 5.3, 0), Region);
        var half = SurfaceIntersection.Intersect(band, Coaxial(3.3, 2, 3.8, 1.5), Region);
        var fullSpan = ((SpiralArc3d)full[0]).Domain;
        var halfSpan = ((SpiralArc3d)Assert.Single(half)).Domain;
        Assert.True(halfSpan.Length < fullSpan.Length,
            $"expected the shorter generator to clip the cut, got {halfSpan.Length} vs {fullSpan.Length}");
        Assert.True(halfSpan.Start >= fullSpan.Start - 1e-9 && halfSpan.End <= fullSpan.End + 1e-9);
    }

    [Fact]
    public void ANonCoaxialCylinderStillFallsToTheTracer()
    {
        // The analytic family is COAXIAL carriers only — a cross-hole is a genuinely
        // transcendental system and must keep taking the marching path, so this asserts
        // the new cases cannot capture it by accident.
        var band = Flank();
        var across = new CylinderSurface((0, 0, 1), Vector3d.UnitZ, Vector3d.UnitX, 1.5);
        foreach (var curve in SurfaceIntersection.Intersect(band, across, Region))
            Assert.IsNotType<SpiralArc3d>(curve);
    }

    [Fact]
    public void APlanarSpiralArcIsBitIdenticalToTheThreeArgumentForm()
    {
        // The cap cuts of every threaded rod ride the planar path; adding an axial term to
        // the type must not move a single bit of them (adding a zero VECTOR is not a no-op
        // on a -0.0 coordinate, which is why PointAt branches on IsPlanar).
        var frame = Frame3d.FromOrthonormal((1, 2, 3), Vector3d.UnitY, Vector3d.UnitZ);
        var planar = new SpiralArc3d(frame, 4, 0.3, new Interval(-1, 2));
        var general = new SpiralArc3d(frame, 4, 0.3, 0, 0, new Interval(-1, 2));
        Assert.True(planar.IsPlanar);
        for (int i = 0; i <= 32; i++)
        {
            double t = planar.Domain.ParameterAt(i / 32.0);
            var (p, q) = (planar.PointAt(t), general.PointAt(t));
            Assert.Equal(BitConverter.DoubleToInt64Bits(p.X), BitConverter.DoubleToInt64Bits(q.X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(p.Y), BitConverter.DoubleToInt64Bits(q.Y));
            Assert.Equal(BitConverter.DoubleToInt64Bits(p.Z), BitConverter.DoubleToInt64Bits(q.Z));
        }
    }

    [Fact]
    public void TheConicalSpiralsDerivativesAreExact()
    {
        var frame = Frame3d.FromOrthonormal((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var spiral = new SpiralArc3d(frame, 5, 0.4, 2, 0.25, new Interval(0.5, 3));
        // Steps chosen per ORDER: a central first difference balances truncation h^2 and
        // round-off eps/h near h = 1e-5, a second difference near 1e-4 (eps/h^2).
        const double h1 = 1e-5, h2 = 1e-4;
        for (int i = 1; i < 8; i++)
        {
            double t = spiral.Domain.ParameterAt(i / 8.0);
            var d = (spiral.PointAt(t + h1) - spiral.PointAt(t - h1)) / (2 * h1);
            var dd = (spiral.PointAt(t + h2) - spiral.PointAt(t) * 2 + spiral.PointAt(t - h2)) / (h2 * h2);
            Assert.True(spiral.DerivativeAt(t).DistanceTo(d) < 1e-8,
                $"first derivative off by {spiral.DerivativeAt(t).DistanceTo(d):E3}");
            Assert.True(spiral.SecondDerivativeAt(t).DistanceTo(dd) < 1e-4,
                $"second derivative off by {spiral.SecondDerivativeAt(t).DistanceTo(dd):E3}");
        }
    }
}
