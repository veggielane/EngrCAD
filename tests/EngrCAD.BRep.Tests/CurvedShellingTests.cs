using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Curved-face <see cref="Shelling"/> — the tier the corner machinery unblocked. Topology,
/// exact wall thickness and exact rim geometry here; tessellated volumes live in Interop.Tests
/// where a mesh is available.
/// </summary>
public class CurvedShellingTests
{
    private static BrepFace TopCapOf(BrepSolid solid) =>
        solid.Faces.Single(f => f.Surface is PlaneSurface p
            && p.Normal.Normalized().Dot(Vector3d.UnitZ) > 0.99
            && !f.IsReversed);

    // ---- offset ----

    [Fact]
    public void OffsetOfACylinder_GrowsTheWallAndBothCapsExactly()
    {
        var grown = Shelling.Offset(SolidFactory.MakeCylinder(5, 10), 1.5);
        grown.Validate();

        var wall = Assert.IsType<CylinderSurface>(
            grown.Faces.Single(f => f.Surface is CylinderSurface).Surface);
        Assert.Equal(6.5, wall.Radius, 12);

        // The caps moved a full 1.5 each way: the offset solid is 6.5 x 13.
        double top = grown.Faces.Where(f => f.Surface is PlaneSurface)
            .Max(f => ((PlaneSurface)f.Surface).Origin.Z);
        double bottom = grown.Faces.Where(f => f.Surface is PlaneSurface)
            .Min(f => ((PlaneSurface)f.Surface).Origin.Z);
        Assert.Equal(11.5, top, 12);
        Assert.Equal(-1.5, bottom, 12);

        // The rims followed: exact circles of the grown radius at the moved heights.
        foreach (var edge in grown.Edges)
        {
            var point = edge.Curve.PointAt(edge.Domain.Start);
            Assert.Equal(6.5, Math.Sqrt(point.X * point.X + point.Y * point.Y), 9);
            Assert.True(Math.Abs(point.Z - 11.5) < 1e-9 || Math.Abs(point.Z + 1.5) < 1e-9);
        }
    }

    [Fact]
    public void OffsetOfACylinder_KeepsEveryRimAtTheOriginalPhase()
    {
        // The phase-alignment rule made checkable: a rim circle whose X rotated would land off
        // its carrier's tessellation grid and crack the band.
        var cylinder = SolidFactory.MakeCylinder(5, 10);
        var seamAngles = cylinder.Edges
            .Select(e => Math.Atan2(e.Curve.PointAt(e.Domain.Start).Y, e.Curve.PointAt(e.Domain.Start).X))
            .ToList();
        var grown = Shelling.Offset(cylinder, 1);
        foreach (var edge in grown.Edges)
        {
            var seam = edge.Curve.PointAt(edge.Domain.Start);
            Assert.Contains(seamAngles, angle => Math.Abs(Math.Atan2(seam.Y, seam.X) - angle) < 1e-12);
        }
    }

    [Fact]
    public void OffsetOfAConeFrustum_StaysACone_WithTheSlantMovedByTheDistance()
    {
        var cone = SolidFactory.MakeCone(10, 4, 12);
        var shrunk = Shelling.Offset(cone, -1);
        shrunk.Validate();

        var side = Assert.IsType<RevolvedSurface>(
            shrunk.Faces.Single(f => f.Surface is RevolvedSurface).Surface);
        Assert.IsType<Line3d>(side.Generator);

        // The caps moved inward by 1 each; the rim radii shrink by 1/sin(half-angle-complement),
        // i.e. by the perpendicular offset expressed radially: 1 / cos(slope).
        // Slope: radius falls 6 over height 12, so the slant direction is (-6, 12)/|..|.
        double slantLength = Math.Sqrt(6 * 6 + 12 * 12);
        double radialShrink = 1 * slantLength / 12; // 1 / cos(angle between slant and axis)
        var rims = shrunk.Edges
            .Select(e => e.Curve.PointAt(e.Domain.Start))
            .OrderBy(p => p.Z)
            .ToList();
        Assert.Equal(2, rims.Count);
        Assert.Equal(1.0, rims[0].Z, 9);
        Assert.Equal(11.0, rims[1].Z, 9);
        Assert.Equal(10 - radialShrink - 0.5, Radius(rims[0]), 9);
        Assert.Equal(4 - radialShrink + 0.5, Radius(rims[1]), 9);
    }

    // ---- shell ----

    [Fact]
    public void ShellingACylinderThroughItsTop_IsACupWithTheExactWallEverywhere()
    {
        var cylinder = SolidFactory.MakeCylinder(5, 10);
        var top = TopCapOf(cylinder);
        var cup = Shelling.Shell(cylinder, 1, f => ReferenceEquals(f, top));
        cup.Validate();

        // One shell (the cavity opens through the top), and the rim annulus closes it.
        Assert.Single(cup.Shells);

        // The wall: an inner cylinder of radius 4 facing INTO the cavity.
        var inner = cup.Faces.Single(f => f.Surface is CylinderSurface c && c.Radius < 5);
        Assert.Equal(4, ((CylinderSurface)inner.Surface).Radius, 12);
        Assert.True(inner.IsReversed, "the cavity wall's outward normal points at the axis");

        // The floor: a plane at z = 1 facing UP into the cavity. "Outward" means away from
        // MATERIAL, and the material is below it, so a cavity floor's normal points up.
        var floor = cup.Faces.Single(f => f.Surface is PlaneSurface p
            && Math.Abs(p.Origin.Z - 1) < 1e-9);
        Assert.Equal(1, ((PlaneSurface)floor.Surface).Normal.Normalized().Z, 12);

        // Every inner rim sits exactly one wall thickness in from its outer twin.
        var innerRims = cup.Edges
            .Select(e => e.Curve.PointAt(e.Domain.Start))
            .Where(p => Radius(p) < 4.5)
            .ToList();
        Assert.Equal(2, innerRims.Count);
        foreach (var rim in innerRims)
            Assert.Equal(4, Radius(rim), 9);
        Assert.Contains(innerRims, p => Math.Abs(p.Z - 1) < 1e-9);   // the floor rim
        Assert.Contains(innerRims, p => Math.Abs(p.Z - 10) < 1e-9);  // the opening rim
    }

    [Fact]
    public void ShellingACylinderWithNoOpening_LeavesASealedVoidAsASecondShell()
    {
        var sealed_ = Shelling.Shell(SolidFactory.MakeCylinder(5, 10), 1);
        sealed_.Validate();
        Assert.Equal(2, sealed_.Shells.Count);

        // The void is the inner cylinder, closed by two flipped caps.
        var cavity = sealed_.Shells[1];
        Assert.Equal(3, cavity.Faces.Count);
        Assert.All(cavity.Faces, f => Assert.True(
            f.IsReversed || f.Surface is PlaneSurface,
            "a cavity face faces into the void"));
    }

    [Fact]
    public void ShellingAConeFrustum_KeepsTheInnerSideAnExactCone()
    {
        var cone = SolidFactory.MakeCone(10, 4, 12);
        var top = TopCapOf(cone);
        var shelled = Shelling.Shell(cone, 1.5, f => ReferenceEquals(f, top));
        shelled.Validate();

        var innerSide = shelled.Faces.Single(f => f.Surface is RevolvedSurface && f.IsReversed);
        var surface = (RevolvedSurface)innerSide.Surface;
        Assert.IsType<Line3d>(surface.Generator);

        // Wall thickness: every sample of the inner cone is exactly 1.5 from the outer one,
        // measured perpendicular to the outer slant.
        var outer = (RevolvedSurface)shelled.Faces
            .Single(f => f.Surface is RevolvedSurface && !f.IsReversed).Surface;
        var a = outer.Generator.PointAt(outer.Generator.Domain.Start);
        var direction = (outer.Generator.PointAt(outer.Generator.Domain.End) - a).Normalized();
        for (int i = 0; i <= 8; i++)
        {
            double v = surface.Generator.Domain.ParameterAt(i / 8.0);
            var point = surface.PointAt(0.7, v);
            Assert.Equal(-1.5, PerpendicularOffset(outer, a, direction, point), 9);
        }
    }

    [Fact]
    public void ShellingAPipeElbowThroughBothEnds_IsAGenusOneTube()
    {
        var elbow = Elbow(majorRadius: 20, tubeRadius: 5, sweep: Math.PI / 2);
        var caps = elbow.Faces.Where(f => f.Surface is PlaneSurface).ToList();
        Assert.Equal(2, caps.Count);

        var tube = Shelling.Shell(elbow, 1, caps.Contains);
        tube.Validate();
        Assert.Single(tube.Shells);

        // Both inner bands are halves of ONE concentric torus: minor radius 4 on the same
        // sweep circle. That the two tangent halves agree is the whole tangent-junction story.
        var innerBands = tube.Faces.Where(f => f.Surface is RevolvedSurface && f.IsReversed).ToList();
        Assert.Equal(2, innerBands.Count);
        foreach (var band in innerBands)
        {
            var centre = CircleFit(((RevolvedSurface)band.Surface).Generator, out double minor);
            Assert.Equal(4, minor, 9);
            Assert.Equal(20, Math.Sqrt(centre.X * centre.X + centre.Y * centre.Y), 9);
        }

        // Every cap rim moved radially only: the cap PLANES do not move (they are openings),
        // so the inner rims sit in them at the reduced tube radius.
        foreach (var cap in tube.Faces.Where(f => f.Surface is PlaneSurface))
        {
            var plane = (PlaneSurface)cap.Surface;
            foreach (var coedge in cap.Loops.SelectMany(l => l.Coedges))
            {
                for (int i = 0; i <= 8; i++)
                {
                    var point = coedge.Edge.Curve.PointAt(coedge.Edge.Domain.ParameterAt(i / 8.0));
                    Assert.True(Math.Abs((point - plane.Origin).Dot(plane.Normal.Normalized())) < 1e-9,
                        "an opening's rim stays in the opening's own plane");
                }
            }
        }
    }

    // ---- refusals, by name ----

    [Fact]
    public void ShellingAnElbowSealedAtBothEnds_IsRefusedByName()
    {
        // Moving the cap planes cuts the offset torus in a quartic, not a circle: the
        // construction's hypothesis is false and the refusal says so rather than approximating.
        var elbow = Elbow(majorRadius: 20, tubeRadius: 5, sweep: Math.PI / 2);
        var exception = Assert.Throws<NotSupportedException>(() => Shelling.Shell(elbow, 1));
        Assert.Contains("concentric circle", exception.Message);
    }

    [Fact]
    public void AnOffsetThatConsumesTheWall_IsRefusedByName()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => Shelling.Offset(SolidFactory.MakeCylinder(5, 10), -6));
        Assert.Contains("consumes", exception.Message);
    }

    [Fact]
    public void PolyhedralSolidsStillTakeThePolyhedralPath_Unchanged()
    {
        // The switch is a property of the INPUT: an all-planar solid must not notice that a
        // curved tier now exists. A box's offset is still the three-plane solve's answer.
        var offset = Shelling.Offset(SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 3, 4))), 0.5);
        offset.Validate();
        Assert.Equal(6, offset.Faces.Count());
        Assert.All(offset.Faces, f => Assert.IsType<PlaneSurface>(f.Surface));
        Assert.All(offset.Edges, e => Assert.IsType<Line3d>(e.Curve));
    }

    // ---- helpers ----

    private static double Radius(in Vector3d point) => Math.Sqrt(point.X * point.X + point.Y * point.Y);

    /// <summary>A quarter-turn pipe elbow: a closed circular profile partially revolved.</summary>
    private static BrepSolid Elbow(double majorRadius, double tubeRadius, double sweep)
    {
        var tubeCentre = new Vector3d(majorRadius, 0, 0);
        var outerArc = NurbsCurve.Arc(
            tubeCentre, Vector3d.UnitX, Vector3d.UnitZ, tubeRadius, -Math.PI / 2, Math.PI / 2);
        var innerArc = NurbsCurve.Arc(
            tubeCentre, Vector3d.UnitX, Vector3d.UnitZ, tubeRadius, Math.PI / 2, 3 * Math.PI / 2);
        return SolidFactory.Revolve(
            new Profile([outerArc, innerArc]), Vector3d.Zero, Vector3d.UnitZ, sweep);
    }

    private static double PerpendicularOffset(
        RevolvedSurface cone, in Vector3d generatorStart, in Vector3d generatorDirection, in Vector3d point)
    {
        var axis = cone.AxisDirection;
        double Height(in Vector3d p) => (p - cone.AxisOrigin).Dot(axis);
        double Radial(in Vector3d p)
        {
            var offset = p - cone.AxisOrigin;
            return (offset - axis * offset.Dot(axis)).Length;
        }
        var a = new Vector2d(Radial(generatorStart), Height(generatorStart));
        var direction = new Vector2d(
            Radial(generatorStart + generatorDirection) - a.X,
            Height(generatorStart + generatorDirection) - a.Y).Normalized();
        var normal = new Vector2d(direction.Y, -direction.X);
        return (new Vector2d(Radial(point), Height(point)) - a).Dot(normal);
    }

    private static Vector3d CircleFit(Curve3d curve, out double radius)
    {
        var domain = curve.Domain;
        var p0 = curve.PointAt(domain.Start);
        var p1 = curve.PointAt(domain.ParameterAt(1.0 / 3));
        var p2 = curve.PointAt(domain.ParameterAt(2.0 / 3));
        var u = p1 - p0;
        var v = p2 - p0;
        var plane = u.Cross(v);
        var centre = p0 + (plane.Cross(u) * v.LengthSquared + v.Cross(plane) * u.LengthSquared)
            / (2 * plane.LengthSquared);
        radius = centre.DistanceTo(p0);
        return centre;
    }
}
