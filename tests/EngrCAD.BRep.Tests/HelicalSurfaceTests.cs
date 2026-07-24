using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class HelicalSurfaceTests
{
    private const double Pitch = 1.25;
    private static readonly double Rate = Pitch / (2 * Math.PI);

    private static HelicalSurface FlankBand(double length = 10) =>
        // An ISO-like leading flank: r from 3.3 to 4.0 while z runs 5P/16 axially.
        new(Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
            new Vector2d(3.3, 0.3), new Vector2d(4.0, 0.3 + 5 * Pitch / 16), Pitch,
            new Interval(-(0.3 + 5 * Pitch / 16) / Rate, (length - 0.3) / Rate));

    [Fact]
    public void SpiralArc_PointAndDerivativesAreExact()
    {
        var frame = Frame3d.FromXY(new Vector3d(1, 2, 3), Vector3d.UnitX, Vector3d.UnitY);
        var arc = new SpiralArc3d(frame, 2.0, 0.15, new Interval(-1.0, 2.5));

        // Point matches the closed form.
        double t = 0.8;
        double r = 2.0 + 0.15 * t;
        var expected = frame.Origin + frame.X * (r * Math.Cos(t)) + frame.Y * (r * Math.Sin(t));
        Assert.True(arc.PointAt(t).DistanceTo(expected) < 1e-15);

        // Analytic derivatives agree with central differences (h² accuracy).
        foreach (double s in new[] { -0.9, 0.0, 0.8, 2.4 })
        {
            double h = 1e-6;
            var d1 = (arc.PointAt(s + h) - arc.PointAt(s - h)) / (2 * h);
            var d2 = (arc.PointAt(s + h) - arc.PointAt(s) * 2 + arc.PointAt(s - h)) / (h * h);
            Assert.True(arc.DerivativeAt(s).DistanceTo(d1) < 1e-7);
            Assert.True(arc.SecondDerivativeAt(s).DistanceTo(d2) < 1e-3);
        }

        // Zero slope degenerates to a circle of the base radius.
        var circular = new SpiralArc3d(frame, 2.0, 0, new Interval(0, 1));
        Assert.True(Math.Abs((circular.PointAt(0.5) - frame.Origin).Length - 2.0) < 1e-15);
    }

    [Fact]
    public void SpiralArc_RejectsNonPositiveRadius()
    {
        var frame = Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SpiralArc3d(frame, 1.0, -1.0, new Interval(0, 2))); // radius hits 0 inside
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SpiralArc3d(frame, 1.0, 0, new Interval(1, 1)));    // empty domain
    }

    [Fact]
    public void HelicalSurface_PointMatchesClosedForm_AndNormalIsExact()
    {
        var band = FlankBand();
        double u = 2.7, v = 0.4;
        double r = 3.3 + (4.0 - 3.3) * v;
        double z = 0.3 + 5 * Pitch / 16 * v + Rate * u;
        var expected = new Vector3d(r * Math.Cos(u), r * Math.Sin(u), z);
        Assert.True(band.PointAt(u, v).DistanceTo(expected) < 1e-14);

        // The analytic normal agrees with the finite-difference cross product and is unit.
        foreach (var (uu, vv) in new[] { (0.0, 0.0), (2.7, 0.4), (-1.3, 1.0), (15.0, 0.7) })
        {
            double h = 1e-6;
            var du = (band.PointAt(uu + h, vv) - band.PointAt(uu - h, vv)) / (2 * h);
            var dv = (band.PointAt(uu, Math.Min(vv + h, 1)) - band.PointAt(uu, Math.Max(vv - h, 0)))
                   / (Math.Min(vv + h, 1) - Math.Max(vv - h, 0));
            var numeric = du.Cross(dv).Normalized();
            var exact = band.NormalAt(uu, vv);
            Assert.True(Math.Abs(exact.Length - 1) < 1e-12);
            Assert.True(exact.DistanceTo(numeric) < 1e-6);
        }

        // A constant-radius band (crest/root flat) has a purely radial normal.
        var flat = new HelicalSurface(
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
            new Vector2d(4.0, 0), new Vector2d(4.0, Pitch / 8), Pitch, new Interval(0, 4 * Math.PI));
        var n = flat.NormalAt(1.1, 0.5);
        Assert.True(Math.Abs(n.Z) < 1e-15);
        Assert.True(n.Dot(new Vector3d(Math.Cos(1.1), Math.Sin(1.1), 0)) > 1 - 1e-12);
    }

    [Fact]
    public void HelicalSurface_TryProjectPoint_RoundTripsExactly()
    {
        var band = FlankBand();
        for (int i = 0; i <= 8; i++)
        {
            for (int j = 0; j <= 4; j++)
            {
                double u = band.DomainU.ParameterAt(i / 8.0);
                double v = j / 4.0;
                var p = band.PointAt(u, v);
                Assert.True(band.TryProjectPoint(p, out var uv, 1e-9),
                    $"projection failed at u={u}, v={v}");
                Assert.True(Math.Abs(uv.X - u) < 1e-9 && Math.Abs(uv.Y - v) < 1e-9,
                    $"expected ({u}, {v}), got ({uv.X}, {uv.Y})");
            }
        }

        // Off-surface points are rejected.
        var off = band.PointAt(3.0, 0.5) + band.NormalAt(3.0, 0.5) * 0.05;
        Assert.False(band.TryProjectPoint(off, out _, 1e-8));

        // A helicoid ramp (dz = 0) uses the radius/axial closed form.
        var ramp = new HelicalSurface(
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
            new Vector2d(2.0, 0.5), new Vector2d(3.0, 0.5), Pitch, new Interval(0, 6 * Math.PI));
        var q = ramp.PointAt(7.0, 0.25);
        Assert.True(ramp.TryProjectPoint(q, out var ruv, 1e-9));
        Assert.True(Math.Abs(ruv.X - 7.0) < 1e-9 && Math.Abs(ruv.Y - 0.25) < 1e-9);
    }

    /// <summary>
    /// The weld invariant for a single band: a rail helix built on the surface's own
    /// frame (rotated to the rail's start phase, origin on the z = 0 cap plane)
    /// evaluates within 1e-9 of the surface at the corresponding grid parameters.
    /// </summary>
    [Fact]
    public void RailHelix_WeldsAgainstBandBoundary()
    {
        double length = 10;
        var band = FlankBand(length);
        double turns = length / Pitch;

        // Bottom rail (v = 0): radius 3.3, phase z0 = 0.3 → starts at angle −z0/rate.
        double alpha = -0.3 / Rate;
        var railX = Vector3d.UnitX * Math.Cos(alpha) + Vector3d.UnitY * Math.Sin(alpha);
        var railFrame = Frame3d.FromOrthonormal(Vector3d.Zero, railX, Vector3d.UnitZ.Cross(railX));
        var rail = new Helix3d(railFrame, 3.3, Pitch, turns);

        int segments = 256;
        for (int i = 0; i <= segments; i++)
        {
            double t = rail.Domain.ParameterAt((double)i / segments);
            double u = alpha + t;
            Assert.True(rail.PointAt(t).DistanceTo(band.PointAt(u, 0)) < 1e-9,
                $"rail sample {i} off the band boundary");
        }
    }

    /// <summary>
    /// The weld invariant for two adjacent bands: both evaluate their shared-rail
    /// boundary within 1e-9 of the same helix samples (the flank's v = 1 edge is the
    /// crest's v = 0 edge).
    /// </summary>
    [Fact]
    public void AdjacentBands_ShareRailSamplesWithin1e9()
    {
        double length = 10;
        double z1 = 0.3 + 5 * Pitch / 16;             // shared corner (r = 4.0, z = z1)
        var frame = Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        var flank = FlankBand(length);
        var crest = new HelicalSurface(frame,
            new Vector2d(4.0, z1), new Vector2d(4.0, z1 + Pitch / 8), Pitch,
            new Interval(-(z1 + Pitch / 8) / Rate, (length - z1) / Rate));

        double alpha = -z1 / Rate;
        var railX = Vector3d.UnitX * Math.Cos(alpha) + Vector3d.UnitY * Math.Sin(alpha);
        var railFrame = Frame3d.FromOrthonormal(Vector3d.Zero, railX, Vector3d.UnitZ.Cross(railX));
        var rail = new Helix3d(railFrame, 4.0, Pitch, length / Pitch);

        int segments = 256;
        for (int i = 0; i <= segments; i++)
        {
            double t = rail.Domain.ParameterAt((double)i / segments);
            double u = alpha + t;
            var p = rail.PointAt(t);
            Assert.True(p.DistanceTo(flank.PointAt(u, 1)) < 1e-9, $"flank side cracked at {i}");
            Assert.True(p.DistanceTo(crest.PointAt(u, 0)) < 1e-9, $"crest side cracked at {i}");
        }
    }

    /// <summary>
    /// The cap-cut weld invariant: the spiral arc a cap plane cuts from a band —
    /// r(u) linear in u, built on the band's own axis frame — evaluates within 1e-9 of
    /// the surface along the cut (v solved from the cap height, exact algebra).
    /// </summary>
    [Fact]
    public void CapSpiral_WeldsAgainstBandColumn()
    {
        var band = FlankBand();
        double r0 = 3.3, z0 = 0.3, dr = 0.7, dz = 5 * Pitch / 16;

        // Bottom cap z = 0: v(u) = (0 − z0 − rate·u)/dz ⇒ r(u) = a + b·u.
        double a = r0 - dr * z0 / dz;
        double b = -dr * Rate / dz;
        var spiral = new SpiralArc3d(
            Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY),
            a, b, new Interval(-(z0 + dz) / Rate, -z0 / Rate));

        int segments = 16;
        for (int i = 0; i <= segments; i++)
        {
            double u = spiral.Domain.ParameterAt((double)i / segments);
            double v = (0 - z0 - Rate * u) / dz;
            Assert.True(spiral.PointAt(u).DistanceTo(band.PointAt(u, v)) < 1e-9,
                $"cap spiral sample {i} off the band");
            Assert.True(Math.Abs(spiral.PointAt(u).Z) < 1e-12, "cap cut left the cap plane");
        }
    }

    [Fact]
    public void HelicalSurface_Validates()
    {
        var frame = Frame3d.FromXY(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY);
        Assert.Throws<ArgumentOutOfRangeException>(() => new HelicalSurface(
            frame, new Vector2d(0, 0), new Vector2d(1, 1), Pitch, new Interval(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HelicalSurface(
            frame, new Vector2d(1, 0), new Vector2d(1, 1), 0, new Interval(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HelicalSurface(
            frame, new Vector2d(1, 0), new Vector2d(1, 1), Pitch, new Interval(2, 2)));
        Assert.Throws<ArgumentException>(() => new HelicalSurface(
            frame, new Vector2d(1, 0.5), new Vector2d(1, 0.5), Pitch, new Interval(0, 1)));
    }
}
