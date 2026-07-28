using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="SurfaceOffset"/> and <see cref="SurfaceCorner"/> — the curved-corner
/// re-intersection machinery three operations were blocked on. The properties worth pinning
/// are exactness (an offset stays in its own surface family), phase (frames do not rotate),
/// and honesty (a chordal corner curve is refused unless the caller opts in).
/// </summary>
public class SurfaceCornerTests
{
    private const double Weld = 1e-9;

    // ---- offsets stay in the family ----

    [Fact]
    public void OffsetOfAPlane_IsAParallelPlaneWithTheSameParameterization()
    {
        var plane = new PlaneSurface((1, 2, 3), Vector3d.UnitX, Vector3d.UnitY);
        var offset = Assert.IsType<PlaneSurface>(SurfaceOffset.Offset(plane, 0.75));

        Assert.Equal(plane.XDirection, offset.XDirection);
        Assert.Equal(plane.YDirection, offset.YDirection);
        // u and v mean the same thing on both, which is what keeps a rebuilt loop aligned.
        Assert.Equal(0.75, (offset.PointAt(4, 5) - plane.PointAt(4, 5)).Dot(plane.Normal), 12);
    }

    [Fact]
    public void OffsetOfACylinder_IsACoaxialCylinderOfTheSamePhase()
    {
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 5);
        var offset = Assert.IsType<CylinderSurface>(SurfaceOffset.Offset(cylinder, -1.5));

        Assert.Equal(3.5, offset.Radius, 12);
        Assert.Equal(cylinder.XDirection, offset.XDirection);
        // u = 0 still points the same way: the phase-alignment rule, made checkable.
        Assert.True((offset.PointAt(0, 2) - (Vector3d)(3.5, 0, 2)).Length < Weld);
    }

    [Fact]
    public void OffsetOfACone_IsACone_ItsGeneratorStillAStraightLine()
    {
        // MakeCone's side: a slanted line revolved. Offsetting inward must leave a LINE
        // generator, or every downstream cone recognition stops working.
        var cone = (RevolvedSurface)SolidFactory.MakeCone(10, 4, 12).Faces
            .First(f => f.Surface is RevolvedSurface).Surface;
        var offset = Assert.IsType<RevolvedSurface>(SurfaceOffset.Offset(cone, -1));

        Assert.IsType<Line3d>(offset.Generator);
        Assert.Equal(cone.AxisDirection, offset.AxisDirection);
        Assert.Equal(cone.Angle, offset.Angle, 15);

        // Every sample sits exactly one unit inside the original, measured EXACTLY as the
        // perpendicular distance to the original cone's own (radius, height) profile line —
        // a sampled grid would only report its own resolution.
        var apex = cone.Generator.PointAt(cone.Generator.Domain.Start);
        var slope = (cone.Generator.PointAt(cone.Generator.Domain.End) - apex).Normalized();
        for (int i = 0; i <= 8; i++)
        {
            double v = offset.Generator.Domain.ParameterAt(i / 8.0);
            var point = offset.PointAt(1.1, v);
            Assert.Equal(-1.0, SignedDistanceToConeProfile(cone, apex, slope, point), 12);
        }
    }

    [Fact]
    public void OffsetOfATorusBand_IsAConcentricTorusBand()
    {
        var torus = (RevolvedSurface)SolidFactory.MakeTorus(20, 5).Faces
            .Select(f => f.Surface).OfType<RevolvedSurface>().First();
        var offset = Assert.IsType<RevolvedSurface>(SurfaceOffset.Offset(torus, -1.25));

        // The generator is still a circular arc of the reduced minor radius, on the same
        // tube centre — so the offset really is a torus, not a general canal surface.
        Assert.True(CircularFit(offset.Generator, out var centre, out double radius));
        Assert.Equal(3.75, radius, 9);
        Assert.True(CircularFit(torus.Generator, out var originalCentre, out _));
        Assert.True(centre.DistanceTo(originalCentre) < Weld);
    }

    [Fact]
    public void OffsetOfAnExtrudedArc_IsAConcentricExtrudedArc()
    {
        var arc = new CurveSegment(new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 6), 0, Math.PI / 2);
        var wall = new ExtrudedSurface(arc, (0, 0, 10));
        var offset = Assert.IsType<ExtrudedSurface>(SurfaceOffset.Offset(wall, 2));

        // The surface normal of an extruded CCW arc points outward, so a positive offset
        // grows the radius.
        Assert.True(CircularFit(offset.Generator, out _, out double radius));
        Assert.Equal(8, radius, 9);
        // The trimmed spelling survives, so the arc still samples at even ANGLES.
        var segment = Assert.IsType<CurveSegment>(offset.Generator);
        Assert.Equal(0, segment.BaseStart, 15);
        Assert.Equal(Math.PI / 2, segment.BaseEnd, 15);
    }

    [Fact]
    public void OffsetOfAShearedStraightExtrusion_IsStillExact_BecauseItIsAPlane()
    {
        // A straight generator extruded along ANY direction is a plane, so its offset is a
        // translation — by the UNIT normal, which is the trap: the raw cross product T x d is
        // short by the shear's sine and would offset too little.
        var sheared = new ExtrudedSurface(new Line3d((0, 0, 0), (10, 0, 0)), (0, 4, 10));
        var offset = Assert.IsType<ExtrudedSurface>(SurfaceOffset.Offset(sheared, 1));
        var normal = sheared.NormalAt(5, 0.5);
        Assert.Equal(1.0, (offset.PointAt(5, 0.5) - sheared.PointAt(5, 0.5)).Dot(normal), 12);
    }

    [Fact]
    public void OffsetOfAShearedCurvedExtrusion_IsRefusedByName()
    {
        // A curved generator NOT perpendicular to the direction has a varying normal, so no
        // planar curve offset reproduces it.
        var arc = new CurveSegment(new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 6), 0, Math.PI / 2);
        var sheared = new ExtrudedSurface(arc, (3, 0, 10));
        Assert.False(SurfaceOffset.TryOffset(sheared, 1, out _, out var reason));
        Assert.Contains("offset plane", reason);
    }

    [Fact]
    public void AnOffsetThatConsumesACylinder_IsRefusedByName()
    {
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 2);
        Assert.False(SurfaceOffset.TryOffset(cylinder, -2.5, out _, out var reason));
        Assert.Contains("consumes", reason);
    }

    // ---- corner points ----

    [Fact]
    public void ThreePlanes_TakeThePlanarTierAndLandExactly()
    {
        var a = new PlaneSurface((1, 0, 0), Vector3d.UnitY, Vector3d.UnitZ);
        var b = new PlaneSurface((0, 2, 0), Vector3d.UnitZ, Vector3d.UnitX);
        var c = new PlaneSurface((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY);

        Assert.True(SurfaceCorner.TrySolvePoint([a, b, c], (0, 0, 0), out var corner, out _));
        Assert.Equal(CornerTier.Planar, corner.Tier);
        Assert.True(corner.Point.DistanceTo((1, 2, 3)) < 1e-14);
        Assert.True(corner.Residual < 1e-14);
    }

    [Fact]
    public void APlaneMeetingACylinderAndAPlane_SolvesAnalyticallyToMachinePrecision()
    {
        // The corner of a shelled cylinder's cap: two planes and the cylinder wall.
        var wall = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 5);
        var cap = new PlaneSurface((0, 0, 8), Vector3d.UnitX, Vector3d.UnitY);
        var seam = new PlaneSurface((0, 0, 0), Vector3d.UnitZ, Vector3d.UnitX); // the y = 0 half-plane

        Assert.True(SurfaceCorner.TrySolvePoint([wall, cap, seam], (4, 0.3, 7), out var corner, out _));
        Assert.Equal(CornerTier.Analytic, corner.Tier);
        Assert.True(corner.Point.DistanceTo((5, 0, 8)) < 1e-11);
        Assert.True(corner.Residual < 1e-11);
    }

    [Fact]
    public void AConeMeetingTwoPlanes_SolvesOnTheExactSlantHeight()
    {
        var cone = (RevolvedSurface)SolidFactory.MakeCone(10, 4, 12).Faces
            .First(f => f.Surface is RevolvedSurface).Surface;
        var cut = new PlaneSurface((0, 0, 6), Vector3d.UnitX, Vector3d.UnitY);
        var seam = new PlaneSurface((0, 0, 0), Vector3d.UnitZ, Vector3d.UnitX);

        Assert.True(SurfaceCorner.TrySolvePoint([cone, cut, seam], (6, 0.2, 6), out var corner, out _));
        // Radius interpolates linearly along the cone: 10 → 4 over 12, so 7 at z = 6.
        Assert.True(corner.Point.DistanceTo((7, 0, 6)) < 1e-10);
    }

    [Fact]
    public void AnOverDeterminedCornerReportsItsResidual_AndIsRefusedWhenTheCarriersMiss()
    {
        var a = new PlaneSurface((1, 0, 0), Vector3d.UnitY, Vector3d.UnitZ);
        var b = new PlaneSurface((0, 2, 0), Vector3d.UnitZ, Vector3d.UnitX);
        var c = new PlaneSurface((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY);
        // A fourth plane through the same point: consistent, so least squares lands on it.
        var through = new PlaneSurface((1, 2, 3), Vector3d.UnitX, new Vector3d(0, 1, 1).Normalized());
        Assert.True(SurfaceCorner.TrySolvePoint([a, b, c, through], (0, 0, 0), out var corner, out _));
        Assert.True(corner.Point.DistanceTo((1, 2, 3)) < 1e-10);

        // A fourth plane that misses: the solve is refused rather than averaged silently.
        var missing = new PlaneSurface((1, 2, 9), Vector3d.UnitX, Vector3d.UnitY);
        Assert.False(SurfaceCorner.TrySolvePoint([a, b, c, missing], (0, 0, 0), out _, out var reason));
        Assert.Contains("converge", reason);
    }

    [Fact]
    public void ThreeParallelCarriers_AreRefusedRatherThanReturningInfinity()
    {
        var a = new PlaneSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
        var b = new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var c = new PlaneSurface((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY);
        Assert.False(SurfaceCorner.TrySolvePoint([a, b, c], (0, 0, 0), out _, out var reason));
        Assert.Contains("not a point", reason);
    }

    // ---- corner curves, and the exactness brand ----

    [Fact]
    public void TwoPlanes_GiveTheStraightCornerVerbatim()
    {
        var a = new PlaneSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
        var b = new PlaneSurface((0, 0, 0), Vector3d.UnitY, Vector3d.UnitZ);
        Assert.True(SurfaceCorner.TrySolveCurve(
            a, b, (0, 0, 0), (0, 5, 0), SurfaceCorner.CornerPolicy.ExactOnly, out var corner, out _));
        var line = Assert.IsType<Line3d>(corner.Curve);
        Assert.Equal(CornerTier.Planar, corner.Tier);
        Assert.Equal(new Vector3d(0, 5, 0), line.End);
    }

    [Fact]
    public void APlaneMeetingACylinder_GivesTheExactCircle_NotAPolyline()
    {
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 5);
        var plane = new PlaneSurface((0, 0, 3), Vector3d.UnitX, Vector3d.UnitY);
        Assert.True(SurfaceCorner.TrySolveCurve(
            cylinder, plane, (5, 0, 3), (5, 0, 3), SurfaceCorner.CornerPolicy.ExactOnly, out var corner, out _));

        Assert.Equal(CornerTier.Analytic, corner.Tier);
        Assert.Equal(0, corner.Deviation, 12);
        Assert.IsType<Circle3d>(corner.Curve);
    }

    [Fact]
    public void AChordalCornerIsRefusedUnlessTheCallerOptsIn_AndThenReportsItsDeviation()
    {
        // Two perpendicular cylinders of different radii: the classic non-conic corner.
        var a = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 5);
        var b = new CylinderSurface((0, 0, 0), Vector3d.UnitY, Vector3d.UnitZ, 3);
        // Where the two meet on the +x, +z side.
        var start = FindCrossing(a, b, (5, 0, 3));
        var end = FindCrossing(a, b, (5, 0, -3));

        Assert.False(SurfaceCorner.TrySolveCurve(
            a, b, start, end, SurfaceCorner.CornerPolicy.ExactOnly, out _, out var reason));
        Assert.Contains("tracer", reason);
        Assert.Contains("AllowTraced", reason);

        Assert.True(SurfaceCorner.TrySolveCurve(
            a, b, start, end, SurfaceCorner.CornerPolicy.AllowTraced, out var corner, out var why), why);
        // Opting in is labelled and measured; the number is the honest cost, not a claim.
        Assert.Equal(CornerTier.Traced, corner.Tier);
        Assert.True(corner.Deviation > 0, "a traced corner's chord error is real and must be reported");

        // The ENDS are exact even though the interior is chordal: a corner becomes a vertex.
        Assert.True(corner.Curve.PointAt(corner.Curve.Domain.Start).DistanceTo(start) < 1e-12);
        Assert.True(corner.Curve.PointAt(corner.Curve.Domain.End).DistanceTo(end) < 1e-12);
    }

    // ---- helpers ----

    /// <summary>
    /// Signed distance from a point to a cone, computed in the cone's own (radius, height)
    /// profile plane: negative inside. Exact, so it can measure an offset to 12 places.
    /// </summary>
    private static double SignedDistanceToConeProfile(
        RevolvedSurface cone, in Vector3d generatorStart, in Vector3d generatorDirection, in Vector3d point)
    {
        var axis = cone.AxisDirection;
        double ProfileHeight(in Vector3d p) => (p - cone.AxisOrigin).Dot(axis);
        double ProfileRadius(in Vector3d p)
        {
            var offset = p - cone.AxisOrigin;
            return (offset - axis * offset.Dot(axis)).Length;
        }
        var a = new Vector2d(ProfileRadius(generatorStart), ProfileHeight(generatorStart));
        var direction = new Vector2d(
            generatorDirection.Length > 0 ? ProfileRadius(generatorStart + generatorDirection) - a.X : 0,
            ProfileHeight(generatorStart + generatorDirection) - a.Y).Normalized();
        // Outward 2D normal of a generator traversed counter-clockwise in (r, z) is (dz, −dr).
        var normal = new Vector2d(direction.Y, -direction.X);
        var q = new Vector2d(ProfileRadius(point), ProfileHeight(point));
        return (q - a).Dot(normal);
    }

    private static bool CircularFit(Curve3d curve, out Vector3d centre, out double radius)
    {
        var domain = curve.Domain;
        var p0 = curve.PointAt(domain.Start);
        var p1 = curve.PointAt(domain.Mid);
        var p2 = curve.PointAt(domain.End);
        var u = p1 - p0;
        var v = p2 - p0;
        var plane = u.Cross(v);
        centre = p0 + (plane.Cross(u) * v.LengthSquared + v.Cross(plane) * u.LengthSquared)
            / (2 * plane.LengthSquared);
        radius = centre.DistanceTo(p0);
        for (int i = 0; i <= 16; i++)
        {
            if (Math.Abs(curve.PointAt(domain.ParameterAt(i / 16.0)).DistanceTo(centre) - radius) > 1e-9)
                return false;
        }
        return true;
    }

    /// <summary>A point on both carriers near the seed — the bicylinder crossing.</summary>
    private static Vector3d FindCrossing(CylinderSurface a, CylinderSurface b, in Vector3d seed)
    {
        // Solve |radial about z| = 5 and |radial about x| = 3 with y from the seed's plane.
        double z = seed.Z;
        double y = Math.Sqrt(Math.Max(0, b.Radius * b.Radius - z * z)) * Math.Sign(seed.Y == 0 ? 1 : seed.Y);
        double x = Math.Sqrt(Math.Max(0, a.Radius * a.Radius - y * y));
        return (x, y, z);
    }
}
