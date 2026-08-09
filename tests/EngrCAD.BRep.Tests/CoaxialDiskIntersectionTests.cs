using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// A full-turn revolve whose generator is PERPENDICULAR to its axis is a PLANE restricted
/// to an annulus, and it is exactly what a <c>Shape.Drill</c> tool presents where a
/// <c>Shape.Cylinder</c> presents a <see cref="PlaneSurface"/> cap — the tool being ONE
/// axis-touching revolve. Recognizing it makes an oblique face cut such a disk along an
/// exact CHORD instead of a marching-tracer polyline.
///
/// <para>It is the b = infinity member of <c>TryCoaxialProfileLine</c>'s family, the same
/// one <c>TryCoaxialDisk</c> already recognized against a helical band, and it is guarded
/// by the same scale-free complement: no axial spread beside the radial one.</para>
///
/// <para>The chord's ENDS are what earn the closed form. Each becomes a vertex shared with
/// the neighbouring face — a bore breaking out of a plate's top face ends exactly where the
/// bore wall's own cut starts — so it must be as accurate as the rim it lands on, where the
/// tracer stops up to one march step short of a boundary and never reaches it.</para>
/// </summary>
public class CoaxialDiskIntersectionTests
{
    private const double Radius = 3;
    private static readonly Aabb Region = new((-20, -20, -20), (20, 20, 20));

    /// <summary>A flat disk of the given radii in the z = <paramref name="z"/> plane,
    /// about +Z. An inner radius of 0 is the axis-TOUCHING disk every drill tool has.</summary>
    private static RevolvedSurface Disk(double z, double inner = 0, double outer = Radius) => new(
        new Line3d((inner, 0, z), (outer, 0, z)), (0, 0, z), Vector3d.UnitZ);

    /// <summary>A plane containing the disk's axis direction, offset along +Y — so it
    /// meets a disk about +Z transversally, in a chord.</summary>
    private static PlaneSurface CuttingAt(double y) =>
        new((0, y, 0), Vector3d.UnitX, Vector3d.UnitZ);

    private static (Vector3d Start, Vector3d End) Ends(Curve3d c) =>
        (c.PointAt(c.Domain.Start), c.PointAt(c.Domain.End));

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-2.9)]
    public void APlaneCrossingACoaxialDisk_IsAnExactChord(double offset)
    {
        var curves = SurfaceIntersection.Intersect(CuttingAt(offset), Disk(4), Region);

        var chord = Assert.IsType<Line3d>(Assert.Single(curves));
        var (start, end) = Ends(chord);

        // Both ends sit exactly on the disk's rim: the closed-form square root, not a
        // march that stops short of it.
        foreach (var p in (Vector3d[])[start, end])
        {
            Assert.Equal(4.0, p.Z, 12);
            Assert.Equal(offset, p.Y, 12);
            Assert.Equal(Radius, new Vector3d(p.X, p.Y, 0).Length, 12);
        }
        Assert.Equal(2 * Math.Sqrt(Radius * Radius - offset * offset), start.DistanceTo(end), 12);
    }

    [Fact]
    public void APlaneCrossingACoaxialANNULUS_LeavesTwoChords()
    {
        // The bore removes the middle stretch, which is the one thing an axis-touching
        // disk never needs and a shoulder face or a washer seat always does.
        const double inner = 1.5, offset = 0.5;
        var curves = SurfaceIntersection.Intersect(CuttingAt(offset), Disk(4, inner), Region);

        Assert.Equal(2, curves.Count);
        double outerHalf = Math.Sqrt(Radius * Radius - offset * offset);
        double innerHalf = Math.Sqrt(inner * inner - offset * offset);
        foreach (var curve in curves)
        {
            var (a, b) = Ends(curve);
            Assert.Equal(outerHalf - innerHalf, a.DistanceTo(b), 12);
            foreach (double x in (double[])[Math.Abs(a.X), Math.Abs(b.X)])
                Assert.True(Math.Abs(x - outerHalf) < 1e-12 || Math.Abs(x - innerHalf) < 1e-12,
                    $"an annulus chord must end on one of its two rims, got x = {x}");
        }

        // One chord each side of the bore, not the same one twice.
        double MidX(Curve3d c) => c.PointAt(c.Domain.ParameterAt(0.5)).X;
        Assert.True(MidX(curves[0]) * MidX(curves[1]) < 0,
            "the two chords must lie on opposite sides of the bore");
    }

    [Fact]
    public void APlaneMissingTheDiskEntirely_ReportsNothing()
    {
        Assert.Empty(SurfaceIntersection.Intersect(CuttingAt(5), Disk(4), Region));
    }

    [Fact]
    public void APlaneParallelToTheDisk_IsRefusedRatherThanAnswered()
    {
        // Parallel planes fall through BY NAME so the axis-perpendicular arm keeps its
        // cases verbatim, which is the only configuration in which a disk carrier reached
        // an analytic path before. An offset parallel plane genuinely misses; a COINCIDENT
        // one is a coplanar pair, which belongs to the fusion tier one layer up and must
        // never come back as a chord.
        var offset = new PlaneSurface((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);
        Assert.Empty(SurfaceIntersection.Intersect(offset, Disk(4), Region));

        var coincident = new PlaneSurface((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        Assert.All(
            SurfaceIntersection.Intersect(coincident, Disk(4), Region),
            c => Assert.IsNotType<Line3d>(c));
    }

    [Fact]
    public void ACoaxialCYLINDERIsNotADisk()
    {
        // The complementary half of the same guard: an axial spread beside the radial one
        // is what makes a profile a cone or a cylinder rather than a disk, so a bore wall
        // (a full-turn revolve of an axis-PARALLEL line) must not be read as one.
        //
        // What separates them is asserted GEOMETRICALLY rather than by curve type, and the
        // distinction is worth spelling out because both answers are now straight: a disk's
        // chord lies IN the disk, perpendicular to the axis and at one axial height, while a
        // band's cut runs ALONG the axis, two of them, one either side of the plane's foot.
        // The type test this used to make was a proxy — right only for as long as the band
        // had no closed form and came back as a tracer polyline — and it is exactly the kind
        // of proxy that starts refusing correct answers the moment the tier it stands in for
        // is built (see SurfaceIntersection.TryCylindricalBand).
        var wall = new RevolvedSurface(
            new Line3d((Radius, 0, 0), (Radius, 0, 10)), Vector3d.Zero, Vector3d.UnitZ);
        const double offset = 1.5;
        var curves = SurfaceIntersection.Intersect(CuttingAt(offset), wall, Region);

        Assert.Equal(2, curves.Count);
        double halfChord = Math.Sqrt(Radius * Radius - offset * offset);
        foreach (var curve in curves)
        {
            var (a, b) = Ends(Assert.IsType<Line3d>(curve));
            // ALONG the axis, and spanning the band's own generator extent — not a chord
            // at one height, which is the only thing a disk could have returned.
            Assert.Equal(1.0, Math.Abs((b - a).Normalized().Dot(Vector3d.UnitZ)), 12);
            Assert.Equal(0.0, Math.Min(a.Z, b.Z), 12);
            Assert.Equal(10.0, Math.Max(a.Z, b.Z), 12);
            Assert.Equal(offset, a.Y, 12);
            Assert.Equal(halfChord, Math.Abs(a.X), 12);
        }
        Assert.True(curves.Select(c => Ends(c).Start.X).Aggregate((x, y) => x * y) < 0,
            "the two lines must lie on opposite sides of the plane's foot");
    }

    [Fact]
    public void ACoaxialBandTallerThanTheRegion_StillEndsOnItsOwnGenerator()
    {
        // The band's extent is the restriction that matters, and it is the band's OWN
        // rather than the query region's: PlaneRevolved's "no circle is invented above a
        // blind bore's end" rule, in the parallel-line member of the same family. Here the
        // region is the wider of the two, so a result clipped only to the region would
        // overshoot the generator by 10 in each direction.
        var wall = new RevolvedSurface(
            new Line3d((Radius, 0, 2), (Radius, 0, 6)), Vector3d.Zero, Vector3d.UnitZ);
        foreach (var curve in SurfaceIntersection.Intersect(CuttingAt(0), wall, Region))
        {
            var (a, b) = Ends(curve);
            Assert.Equal(2.0, Math.Min(a.Z, b.Z), 12);
            Assert.Equal(6.0, Math.Max(a.Z, b.Z), 12);
        }
    }

    [Fact]
    public void APartialRevolveIsNotABand()
    {
        // "Lies on the carrier" is not "IS the carrier": every point of a half-turn revolve
        // lies on the full cylinder, so promoting one would report a cut on 180 degrees of
        // surface the face does not carry — the recorded quarter-arc-corner trap. The
        // tracer's answer is whatever it is; what this pins is that the exact tier declines.
        var half = new RevolvedSurface(
            new Line3d((Radius, 0, 0), (Radius, 0, 10)), Vector3d.Zero, Vector3d.UnitZ, Math.PI);
        Assert.All(
            SurfaceIntersection.Intersect(CuttingAt(1.5), half, Region),
            c => Assert.IsNotType<Line3d>(c));
    }

    [Fact]
    public void AConeIsNotABand()
    {
        // The other side of the same partition: a slanted generator has a radial spread as
        // well as an axial one, so it is neither disk nor band and must reach neither exact
        // arm. (A cone cut by a plane containing its axis is a hyperbola, which this tier
        // does not carry.)
        var cone = new RevolvedSurface(
            new Line3d((1, 0, 0), (Radius, 0, 10)), Vector3d.Zero, Vector3d.UnitZ);
        Assert.All(
            SurfaceIntersection.Intersect(CuttingAt(0.5), cone, Region),
            c => Assert.IsNotType<Line3d>(c));
    }

    [Fact]
    public void ABandCutObliquelyIsTheExactEllipseWhileItFits_AndTheTracersOtherwise()
    {
        // The oblique member is accepted only when the whole conic lies inside the band, the
        // wholly-inside rule TryPatchQuadric applies to a bounded patch — one comparison,
        // since the axial coordinate along a conic ranges over centre +/- hypot of the two
        // semi-axis components. A tall band admits the ellipse; a short one does not, and
        // falls through rather than being clipped by this tier.
        var tilted = new PlaneSurface((0, 0, 6), Vector3d.UnitX, new Vector3d(0, 1, 1).Normalized());
        var tall = new RevolvedSurface(
            new Line3d((Radius, 0, -20), (Radius, 0, 20)), Vector3d.Zero, Vector3d.UnitZ);
        var ellipse = Assert.IsType<Ellipse3d>(Assert.Single(
            SurfaceIntersection.Intersect(tilted, tall, Region)));
        Assert.Equal(Radius, Math.Min(ellipse.SemiAxisX.Length, ellipse.SemiAxisY.Length), 12);
        Assert.Equal(Radius * Math.Sqrt(2), Math.Max(ellipse.SemiAxisX.Length, ellipse.SemiAxisY.Length), 12);

        var shallow = new RevolvedSurface(
            new Line3d((Radius, 0, 5), (Radius, 0, 7)), Vector3d.Zero, Vector3d.UnitZ);
        Assert.All(
            SurfaceIntersection.Intersect(tilted, shallow, Region),
            c => Assert.IsNotType<Ellipse3d>(c));
    }

    [Fact]
    public void APlanePerpendicularToABandKeepsItsIncumbentCircle()
    {
        // The perpendicular arm is claimed EARLIER in the switch, so a drilled cap's rim
        // keeps PlaneRevolved's arithmetic and its phase alignment with u = 0 rather than
        // being re-derived through the band. Asserted as a value, since "unchanged" is the
        // whole claim.
        var wall = new RevolvedSurface(
            new Line3d((Radius, 0, 0), (Radius, 0, 10)), Vector3d.Zero, Vector3d.UnitZ);
        var flat = new PlaneSurface((0, 0, 4), Vector3d.UnitX, Vector3d.UnitY);
        var circle = Assert.IsType<Circle3d>(Assert.Single(
            SurfaceIntersection.Intersect(flat, wall, Region)));
        Assert.Equal(Radius, circle.Radius, 12);
        Assert.Equal(new Vector3d(0, 0, 4), circle.Center);
        Assert.Equal(Vector3d.UnitX, circle.XDirection);
    }

    [Fact]
    public void AnObliqueDrillCapMeetsAPlateFaceOnItsExactChord()
    {
        // The construction the rule exists for, at carrier level: a bore drilled along +Y
        // whose flat bottom stops inside a plate whose top face is z = 10, the cap's own
        // pole 1 mm below it. The chord through the pole is the DIAMETRAL member that
        // nothing could tessellate before.
        var cap = new RevolvedSurface(
            new Line3d((0, 0, 9), (Radius, 0, 9)), (0, 0, 9), Vector3d.UnitY);
        var top = new PlaneSurface((0, 0, 10), Vector3d.UnitX, Vector3d.UnitY);

        var chord = Assert.IsType<Line3d>(Assert.Single(
            SurfaceIntersection.Intersect(top, cap, Region)));
        var (a, b) = Ends(chord);

        Assert.Equal(10.0, a.Z, 12);
        Assert.Equal(10.0, b.Z, 12);
        Assert.Equal(0.0, a.Y, 12);
        Assert.Equal(2 * Math.Sqrt(Radius * Radius - 1), a.DistanceTo(b), 12);
    }
}
