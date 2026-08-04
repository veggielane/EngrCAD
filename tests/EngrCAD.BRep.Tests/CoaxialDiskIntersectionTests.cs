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
        var wall = new RevolvedSurface(
            new Line3d((Radius, 0, 0), (Radius, 0, 10)), Vector3d.Zero, Vector3d.UnitZ);
        var curves = SurfaceIntersection.Intersect(CuttingAt(1.5), wall, Region);

        Assert.NotEmpty(curves);
        Assert.All(curves, c => Assert.IsNotType<Line3d>(c));
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
