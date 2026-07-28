using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Curved-face <see cref="Draft"/>. The natural test is that a drafted cylinder is exactly a
/// cone — not a cone to some tolerance, but a <see cref="RevolvedSurface"/> of a straight
/// generator at the requested angle.
/// </summary>
public class CurvedDraftTests
{
    private const double Ten = Math.PI / 18;

    private static BrepFace SideOf(BrepSolid solid) =>
        solid.Faces.Single(f => !f.IsPlanar(out _, out _));

    [Fact]
    public void DraftingACylinder_MakesAnExactCone()
    {
        var cylinder = SolidFactory.MakeCylinder(5, 10);
        var side = SideOf(cylinder);
        // Neutral at the base, pulling up: the classic release taper narrows going up.
        var drafted = Draft.Apply(cylinder, Vector3d.Zero, Vector3d.UnitZ, Ten,
            f => ReferenceEquals(f, side));
        drafted.Validate();
        Assert.True(drafted.SatisfiesEulerFormula(genus: 0));

        var cone = Assert.IsType<RevolvedSurface>(SideOf(drafted).Surface);
        Assert.IsType<Line3d>(cone.Generator);

        // The taper is exact: the base radius is untouched (it is on the neutral plane) and
        // the top has drawn in by exactly height * tan(angle).
        double inset = 10 * Math.Tan(Ten);
        var rims = drafted.Edges
            .Select(e => e.Curve.PointAt(e.Domain.Start))
            .OrderBy(p => p.Z)
            .ToList();
        Assert.Equal(2, rims.Count);
        Assert.Equal(5, Radius(rims[0]), 12);
        Assert.Equal(5 - inset, Radius(rims[1]), 9);

        // ... and the surface itself leans by exactly the angle. Measured off the GENERATOR,
        // not off NormalAt: a revolve's normal comes from the base class's central difference
        // and is only good to ~1e-10, which would be testing the difference and not the taper.
        Assert.Equal(Math.Sin(Ten), Lean(cone), 14);
    }

    [Fact]
    public void DraftingACylinder_LeavesTheNeutralRimExactlyWhereItWas()
    {
        // The defining property of a neutral plane, on a curved face: it is the parting line.
        var cylinder = SolidFactory.MakeCylinder(5, 10);
        var side = SideOf(cylinder);
        var drafted = Draft.Apply(cylinder, (0, 0, 4), Vector3d.UnitZ, Ten, f => ReferenceEquals(f, side));

        // The neutral height is 4, so the drafted cone still passes through radius 5 there.
        var cone = (RevolvedSurface)SideOf(drafted).Surface;
        var generator = cone.Generator;
        double t = 0;
        for (int step = 0; step < 200; step++)
        {
            t = step / 200.0;
            if (generator.PointAt(generator.Domain.ParameterAt(t)).Z >= 4)
                break;
        }
        // Bisect onto z = 4 exactly and read the radius there.
        double lo = 0, hi = 1;
        for (int step = 0; step < 80; step++)
        {
            double mid = (lo + hi) / 2;
            if (generator.PointAt(generator.Domain.ParameterAt(mid)).Z < 4)
                lo = mid;
            else
                hi = mid;
        }
        Assert.Equal(5, Radius(generator.PointAt(generator.Domain.ParameterAt((lo + hi) / 2))), 9);
    }

    [Fact]
    public void DraftingACone_StaysACone_AtTheCombinedAngle()
    {
        // Composability, which the planar path already guarantees: a cone drafted by 10 deg
        // is the cone whose slant is 10 deg steeper.
        var cone = SolidFactory.MakeCone(10, 6, 12);
        var side = SideOf(cone);
        var drafted = Draft.Apply(cone, Vector3d.Zero, Vector3d.UnitZ, Ten, f => ReferenceEquals(f, side));
        drafted.Validate();

        var surface = Assert.IsType<RevolvedSurface>(SideOf(drafted).Surface);
        Assert.IsType<Line3d>(surface.Generator);

        Assert.Equal(
            Math.Asin(Lean((RevolvedSurface)side.Surface)) + Ten, Math.Asin(Lean(surface)), 13);
    }

    [Fact]
    public void DraftingByHalfTwice_EqualsDraftingOnce()
    {
        var cylinder = SolidFactory.MakeCylinder(5, 10);
        var once = Draft.Apply(cylinder, Vector3d.Zero, Vector3d.UnitZ, Ten,
            f => !f.IsPlanar(out _, out _));

        var again = SolidFactory.MakeCylinder(5, 10);
        var half = Draft.Apply(again, Vector3d.Zero, Vector3d.UnitZ, Ten / 2,
            f => !f.IsPlanar(out _, out _));
        var twice = Draft.Apply(half, Vector3d.Zero, Vector3d.UnitZ, Ten / 2,
            f => !f.IsPlanar(out _, out _));

        Assert.Equal(
            Lean((RevolvedSurface)SideOf(once).Surface),
            Lean((RevolvedSurface)SideOf(twice).Surface), 14);
    }

    [Fact]
    public void DraftingAFaceOnAnotherAxis_IsRefusedByName()
    {
        // A cylinder lying across the pull direction has no exact taper: its drafted carrier
        // is not a surface of any family this kernel builds.
        var lying = SolidFactory.MakeCylinder(5, 10);
        var side = SideOf(lying);
        var exception = Assert.Throws<NotSupportedException>(
            () => Draft.Apply(lying, Vector3d.Zero, Vector3d.UnitX, Ten, f => ReferenceEquals(f, side)));
        Assert.Contains("surface of revolution about the pull direction", exception.Message);
    }

    [Fact]
    public void PlanarSolidsStillTakeThePlanarPath()
    {
        // The switch is a property of the INPUT, so a box still drafts through the prism path
        // and comes back with exact PlaneSurface faces.
        var block = SolidFactory.MakeBox(new Aabb((-10, -10, 0), (10, 10, 10)));
        var bottom = block.PlanarFacesWithNormal(-Vector3d.UnitZ).Single();
        var drafted = Draft.Apply(block, bottom, Ten);
        Assert.All(drafted.Faces, f => Assert.IsType<PlaneSurface>(f.Surface));
    }

    private static double Radius(in Vector3d point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);

    /// <summary>
    /// How far a revolved band's outward normal leans toward its own axis direction, read
    /// EXACTLY off the generator's (radius, height) profile: the outward 2D normal of a
    /// generator traversed counter-clockwise is (dz, −dr) normalized, so the lean is −dr/|d|.
    /// Going through <see cref="Surface.NormalAt"/> instead would measure the base class's
    /// central difference, which is good to about 1e-10 and would cap every assertion here.
    /// </summary>
    private static double Lean(RevolvedSurface surface)
    {
        var axis = surface.AxisDirection;
        var domain = surface.Generator.Domain;
        var a = surface.Generator.PointAt(domain.Start) - surface.AxisOrigin;
        var b = surface.Generator.PointAt(domain.End) - surface.AxisOrigin;
        double za = a.Dot(axis), zb = b.Dot(axis);
        double dr = (b - axis * zb).Length - (a - axis * za).Length;
        double dz = zb - za;
        return -dr / Math.Sqrt(dr * dr + dz * dz);
    }
}
