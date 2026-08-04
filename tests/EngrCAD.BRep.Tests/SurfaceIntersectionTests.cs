using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class SurfaceIntersectionTests
{
    private static readonly Aabb Region = new((-3, -3, -3), (3, 3, 3));

    /// <summary>
    /// Sample points to verify: a traced polyline is exact only at its vertices (between
    /// them it is a chord), analytic curves everywhere.
    /// </summary>
    private static IEnumerable<Vector3d> SamplePoints(Curve3d curve)
    {
        if (curve is PolylineCurve3d polyline)
        {
            foreach (var p in polyline.Points)
                yield return p;
        }
        else
        {
            for (int i = 0; i <= 40; i++)
                yield return curve.PointAt(curve.Domain.ParameterAt(i / 40.0));
        }
    }

    private static void AssertOnPlane(Curve3d curve, PlaneSurface plane, double tolerance = 1e-9)
    {
        foreach (var p in SamplePoints(curve))
            Assert.True(Math.Abs((p - plane.Origin).Dot(plane.Normal)) < tolerance, $"point {p} off plane");
    }

    private static void AssertOnCylinder(Curve3d curve, CylinderSurface cylinder, double tolerance = 1e-9)
    {
        foreach (var p in SamplePoints(curve))
        {
            var radial = p - cylinder.Origin;
            radial -= cylinder.Axis * radial.Dot(cylinder.Axis);
            Assert.True(Math.Abs(radial.Length - cylinder.Radius) < tolerance,
                $"point {p} at radius {radial.Length} (expected {cylinder.Radius})");
        }
    }

    private static void AssertOnSphere(Curve3d curve, SphereSurface sphere, double tolerance = 1e-9)
    {
        foreach (var p in SamplePoints(curve))
            Assert.True(Math.Abs(p.DistanceTo(sphere.Center) - sphere.Radius) < tolerance, $"point {p} off sphere");
    }

    [Fact]
    public void PlanePlane_YieldsClippedLine()
    {
        var a = new PlaneSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);      // z = 0
        var b = new PlaneSurface((0, 0, 0), Vector3d.UnitY, Vector3d.UnitZ);      // x = 0
        var curve = Assert.Single(SurfaceIntersection.Intersect(a, b, Region));

        var line = Assert.IsType<Line3d>(curve);
        Assert.True((line.End - line.Start).Normalized().IsParallelTo(Vector3d.UnitY, Tolerance.Default));
        AssertOnPlane(line, a);
        AssertOnPlane(line, b);
        Assert.True(Region.Contains(line.Start) && Region.Contains(line.End));
    }

    [Fact]
    public void ParallelPlanes_NoIntersection()
    {
        var a = new PlaneSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY);
        var b = new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        Assert.Empty(SurfaceIntersection.Intersect(a, b, Region));
    }

    [Fact]
    public void PlaneCylinder_Perpendicular_Circle()
    {
        var plane = new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.5);
        var curve = Assert.Single(SurfaceIntersection.Intersect(plane, cylinder, Region));

        var circle = Assert.IsType<Circle3d>(curve);
        Assert.Equal(1.5, circle.Radius, 12);
        Assert.True(circle.Center.AreEqual((0, 0, 1), Tolerance.Default));
        AssertOnCylinder(circle, cylinder);
        AssertOnPlane(circle, plane);
    }

    [Fact]
    public void PlaneCylinder_Tilted_ExactEllipse()
    {
        double tilt = Math.PI / 5;
        var normal = new Vector3d(Math.Sin(tilt), 0, Math.Cos(tilt));
        var x = normal.ArbitraryPerpendicular(Tolerance.Default);
        var plane = new PlaneSurface((0, 0, 0.3), x, normal.Cross(x));
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);

        var curve = Assert.Single(SurfaceIntersection.Intersect(plane, cylinder, Region));
        var ellipse = Assert.IsType<Ellipse3d>(curve);

        Assert.Equal(1.0 / Math.Cos(tilt), ellipse.SemiAxisX.Length, 12); // semi-major r/cos θ
        Assert.Equal(1.0, ellipse.SemiAxisY.Length, 12);                  // semi-minor r
        AssertOnCylinder(ellipse, cylinder);
        AssertOnPlane(ellipse, plane);
    }

    [Fact]
    public void PlaneCylinder_AxisParallel_TwoLines()
    {
        // Plane y = 0.6 cutting a radius-1 cylinder along Z: two lines at x = ±0.8.
        var plane = new PlaneSurface((0, 0.6, 0), Vector3d.UnitZ, Vector3d.UnitX);
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);
        var curves = SurfaceIntersection.Intersect(plane, cylinder, Region);

        Assert.Equal(2, curves.Count);
        foreach (var curve in curves)
        {
            AssertOnCylinder(curve, cylinder);
            AssertOnPlane(curve, plane);
            Assert.True(((Line3d)curve).TangentAt(0).IsParallelTo(Vector3d.UnitZ, Tolerance.Default));
        }
        // Missing entirely when too far away.
        var far = new PlaneSurface((0, 1.5, 0), Vector3d.UnitZ, Vector3d.UnitX);
        Assert.Empty(SurfaceIntersection.Intersect(far, cylinder, Region));
    }

    [Fact]
    public void PlaneSphere_Circle()
    {
        var plane = new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);
        var sphere = new SphereSurface((0, 0, 0), 2.0);
        var curve = Assert.Single(SurfaceIntersection.Intersect(plane, sphere, Region));

        var circle = Assert.IsType<Circle3d>(curve);
        Assert.Equal(Math.Sqrt(3), circle.Radius, 12);
        AssertOnSphere(circle, sphere);
        AssertOnPlane(circle, plane);
    }

    [Fact]
    public void SphereSphere_Circle()
    {
        var a = new SphereSurface((0, 0, 0), 2.0);
        var b = new SphereSurface((3, 0, 0), 2.0);
        var curve = Assert.Single(SurfaceIntersection.Intersect(a, b, Region));

        var circle = Assert.IsType<Circle3d>(curve);
        AssertOnSphere(circle, a);
        AssertOnSphere(circle, b);
        Assert.True(circle.Center.AreEqual((1.5, 0, 0), Tolerance.Default));

        Assert.Empty(SurfaceIntersection.Intersect(a, new SphereSurface((5, 0, 0), 2.0), Region));
    }

    [Fact]
    public void ParallelCylinders_TwoLines()
    {
        var a = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);
        var b = new CylinderSurface((1.2, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);
        var curves = SurfaceIntersection.Intersect(a, b, Region);

        Assert.Equal(2, curves.Count);
        foreach (var curve in curves)
        {
            AssertOnCylinder(curve, a);
            AssertOnCylinder(curve, b);
        }
    }

    [Fact]
    public void Marching_CrossingCylinders_TwoClosedBranches()
    {
        // Perpendicular equal-radius cylinders: the intersection is two closed curves
        // (degenerate Steinmetz: two ellipses).
        var a = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);  // axis Z
        var b = new CylinderSurface((0, 0, 0), Vector3d.UnitY, Vector3d.UnitZ, 1.0);  // axis X
        var curves = SurfaceIntersection.Intersect(a, b, Region);

        Assert.Equal(2, curves.Count);
        foreach (var curve in curves)
        {
            Assert.True(curve.IsClosed, "Steinmetz branches are closed");
            AssertOnCylinder(curve, a, 1e-8);
            AssertOnCylinder(curve, b, 1e-8);
        }
    }

    [Fact]
    public void Marching_UnequalCrossingCylinders_OnBothSurfaces()
    {
        var a = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);   // axis Z
        var b = new CylinderSurface((0.4, 0, 0), Vector3d.UnitY, Vector3d.UnitZ, 0.5); // axis X, offset
        var curves = SurfaceIntersection.Intersect(a, b, Region);

        Assert.NotEmpty(curves);
        foreach (var curve in curves)
        {
            AssertOnCylinder(curve, a, 1e-8);
            AssertOnCylinder(curve, b, 1e-8);
        }
    }

    [Fact]
    public void Marching_SphereCylinder_Viviani()
    {
        // Viviani's curve: sphere radius 2, cylinder radius 1 tangent through the center.
        var sphere = new SphereSurface((0, 0, 0), 2.0);
        var cylinder = new CylinderSurface((1, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);
        var curves = SurfaceIntersection.Intersect(sphere, cylinder, Region);

        Assert.NotEmpty(curves);
        double maxZ = double.NegativeInfinity;
        foreach (var curve in curves)
        {
            AssertOnSphere(curve, sphere, 1e-8);
            AssertOnCylinder(curve, cylinder, 1e-8);
            foreach (var p in SamplePoints(curve))
                maxZ = Math.Max(maxZ, p.Z);
        }
        Assert.True(maxZ > 1.5, $"Viviani's curve reaches z = ±2·sin(...) near the pole; got max z {maxZ}");
    }

    [Fact]
    public void Marching_DisjointSurfaces_NoCurves()
    {
        var a = new SphereSurface((0, 0, 0), 0.5);
        var b = new CylinderSurface((2, 2, 0), Vector3d.UnitX, Vector3d.UnitY, 0.3);
        Assert.Empty(SurfaceIntersection.Intersect(a, b, Region));
    }

    [Fact]
    public void PlaneParallelToAxis_SphereCarrierRevolved_ExactFullCircle()
    {
        // A hemisphere of MakeSphere is a full-turn RevolvedSurface whose generator arc
        // lies on a sphere centered on the axis. A plane parallel to the axis must get
        // the exact analytic circle of the CARRIER sphere (the marching tracer would
        // clip an open polyline at the bounded generator, whose loose ends can never
        // refine against face boundaries). The curve may run past the bounded surface —
        // the face splitter clips per face.
        var hemisphere = (RevolvedSurface)SolidFactory.MakeSphere(2).Faces.First().Surface;
        var plane = new PlaneSurface((1.5, 0, 0), Vector3d.UnitY, Vector3d.UnitZ); // x = 1.5

        var curve = Assert.Single(SurfaceIntersection.Intersect(plane, hemisphere, Region));
        var circle = Assert.IsType<Circle3d>(curve);
        Assert.True(circle.IsClosed);
        Assert.Equal(Math.Sqrt(4 - 2.25), circle.Radius, 12);
        Assert.Equal(0, circle.Center.DistanceTo((1.5, 0, 0)), 12);
        foreach (var p in SamplePoints(circle))
        {
            Assert.Equal(2, p.DistanceTo(Vector3d.Zero), 12); // exactly on the sphere
            AssertOnPlane(circle, plane, 1e-12);
        }
    }

    [Fact]
    public void PlanePerpendicularToAxis_SphereCarrierRevolved_StillPhaseAligned()
    {
        // Perpendicular planes must keep the pre-existing phase-aligned path (circles
        // aligned with the band's u = 0) — the sphere-carrier case must not shadow it.
        var hemisphere = (RevolvedSurface)SolidFactory.MakeSphere(2).Faces
            .First(f => f.Loops[0].Coedges[0].SameSense).Surface; // northern half
        var plane = new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY); // z = 1

        var curve = Assert.Single(SurfaceIntersection.Intersect(plane, hemisphere, Region));
        var circle = Assert.IsType<Circle3d>(curve);
        Assert.Equal(Math.Sqrt(3), circle.Radius, 12);
        // Phase alignment: the circle starts on the u = 0 generator half-plane — for
        // MakeSphere that is the +X half of the XZ plane (never an arbitrary frame).
        var start = circle.PointAt(circle.Domain.Start);
        Assert.Equal(0, start.Y, 12);
        Assert.True(start.X > 0, $"start {start} should sit on the +X generator half-plane");
    }

    // ---- bounded planar carriers: extrusions of straight / arc generators ----

    // A pocket-sized wall (an extruded profile segment) inside a plate-sized region: the
    // region is 6× longer than the wall, so a region-clipped carrier line is obvious.
    private static readonly Aabb PlateRegion = new((-31, -11, -3), (31, 11, 3));

    private static ExtrudedSurface PocketWall(double y = -2.5) =>
        new(new Line3d((-5, y, 1), (5, y, 1)), (0, 0, 1.5));

    [Fact]
    public void PlaneAcrossExtrudedLine_SectionSpansTheWholeGenerator()
    {
        // THE regression: a box's top plane cutting a sketch pocket's wall. The marching
        // tracer stopped up to one march step short of each generator end, so the four
        // walls' cuts never met at the pocket corners and the boolean left single-use
        // edges (an open mesh, silently). The exact section must reach both ends.
        var wall = PocketWall();
        var top = new PlaneSurface((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY);

        var curve = Assert.Single(SurfaceIntersection.Intersect(top, wall, PlateRegion));
        Assert.Equal(0, curve.PointAt(curve.Domain.Start).DistanceTo((-5, -2.5, 2)), 12);
        Assert.Equal(0, curve.PointAt(curve.Domain.End).DistanceTo((5, -2.5, 2)), 12);
        Assert.IsType<Line3d>(curve.Underlying);
        AssertOnPlane(curve, top, 1e-12);
    }

    [Fact]
    public void PlaneAcrossExtrudedSegments_AdjacentSectionsShareCornersExactly()
    {
        // The welding invariant that lets a pocket outline close into a chain: the
        // section is the generator TRANSLATED, so two profile segments sharing a corner
        // hand over that corner BIT-FOR-BIT (no projection, no re-derivation).
        var corner = new Vector3d(5, -2.5, 1);
        var first = new ExtrudedSurface(new Line3d((-5, -2.5, 1), corner), (0, 0, 1.5));
        var second = new ExtrudedSurface(new Line3d(corner, (5, 2.5, 1)), (0, 0, 1.5));
        var top = new PlaneSurface((0, 0, 2), Vector3d.UnitX, Vector3d.UnitY);

        var a = Assert.Single(SurfaceIntersection.Intersect(top, first, PlateRegion));
        var b = Assert.Single(SurfaceIntersection.Intersect(top, second, PlateRegion));
        var end = a.PointAt(a.Domain.End);
        var start = b.PointAt(b.Domain.Start);
        Assert.Equal(end.X, start.X);
        Assert.Equal(end.Y, start.Y);
        Assert.Equal(end.Z, start.Z);
    }

    [Fact]
    public void PlaneAcrossExtrudedArc_SectionIsTheExactArc()
    {
        // Slot ends and rounded corners: the section path is generator-shape agnostic,
        // so a rational arc stays an exact arc instead of a chorded tracer polyline.
        var arc = NurbsCurve.Arc((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 2, 0, Math.PI);
        var wall = new ExtrudedSurface(arc, (0, 0, 4));
        var cut = new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);

        var curve = Assert.Single(SurfaceIntersection.Intersect(cut, wall, Region));
        AssertOnPlane(curve, cut, 1e-12);
        for (int i = 0; i <= 40; i++)
        {
            var p = curve.PointAt(curve.Domain.ParameterAt(i / 40.0));
            Assert.Equal(2, new Vector3d(p.X, p.Y, 0).Length, 12); // exactly on the arc's radius
        }
        Assert.Equal(0, curve.PointAt(curve.Domain.Start).DistanceTo((2, 0, 1)), 12);
        Assert.Equal(0, curve.PointAt(curve.Domain.End).DistanceTo((-2, 0, 1)), 12);
    }

    [Fact]
    public void PlaneFlushWithAnExtrusionRim_ReportsNoSection()
    {
        // A plane through the extrusion's own start or end rim is the coplanar/tangent
        // configuration booleans reject; splitting there would only make zero-extent
        // slivers. Both rims and a plane that misses the extrusion give no curve.
        var wall = PocketWall();
        foreach (double z in (ReadOnlySpan<double>)[1.0, 2.5, 4.0])
        {
            var plane = new PlaneSurface((0, 0, z), Vector3d.UnitX, Vector3d.UnitY);
            Assert.Empty(SurfaceIntersection.Intersect(plane, wall, PlateRegion));
        }
    }

    [Fact]
    public void AngledPlaneAcrossExtrudedLine_ClipsToTheGeneratorNotTheRegion()
    {
        // The bounded-patch path. An extrusion of a straight generator IS a plane, but a
        // BOUNDED one: clipping the analytic line to the query region alone would return
        // a 60-long line that slices clean across neighbouring pockets.
        var wall = PocketWall();
        var side = new PlaneSurface((1, 0, 0), Vector3d.UnitY, Vector3d.UnitZ); // x = 1

        var curve = Assert.Single(SurfaceIntersection.Intersect(side, wall, PlateRegion));
        var line = Assert.IsType<Line3d>(curve);
        var (lo, hi) = line.Start.Z < line.End.Z ? (line.Start, line.End) : (line.End, line.Start);
        Assert.Equal(0, lo.DistanceTo((1, -2.5, 1)), 12);   // the wall's bottom rim
        Assert.Equal(0, hi.DistanceTo((1, -2.5, 2.5)), 12); // the wall's top rim
    }

    [Fact]
    public void TwoExtrudedLines_ClipToBothPatches()
    {
        // Two straight-walled extrusions crossing: the shared vertical line is clipped
        // to the overlap of both parallelograms (here the shorter wall's v-range).
        var tall = new ExtrudedSurface(new Line3d((-5, 2, 0), (5, 2, 0)), (0, 0, 4));
        var shortWall = new ExtrudedSurface(new Line3d((1, -6, 1), (1, 6, 1)), (0, 0, 2));

        var curve = Assert.Single(SurfaceIntersection.Intersect(tall, shortWall, PlateRegion));
        var line = Assert.IsType<Line3d>(curve);
        var (lo, hi) = line.Start.Z < line.End.Z ? (line.Start, line.End) : (line.End, line.Start);
        Assert.Equal(0, lo.DistanceTo((1, 2, 1)), 12);
        Assert.Equal(0, hi.DistanceTo((1, 2, 3)), 12);
    }

    [Fact]
    public void ExtrudedLinesOnParallelPlanes_NoCurve()
    {
        var a = new ExtrudedSurface(new Line3d((-5, 2, 0), (5, 2, 0)), (0, 0, 4));
        var b = new ExtrudedSurface(new Line3d((-5, 3, 0), (5, 3, 0)), (0, 0, 4));
        Assert.Empty(SurfaceIntersection.Intersect(a, b, PlateRegion));
    }

    [Fact]
    public void ExtrudedLinesOnDisjointPatches_NoCurve()
    {
        // Carrier planes that cross, but parallelograms that never meet: the trap the
        // unclipped promotion falls into (an unrelated pocket getting sliced).
        var a = new ExtrudedSurface(new Line3d((-5, 2, 0), (5, 2, 0)), (0, 0, 4));
        var b = new ExtrudedSurface(new Line3d((20, -6, 0), (20, 6, 0)), (0, 0, 4));
        Assert.Empty(SurfaceIntersection.Intersect(a, b, PlateRegion));
    }

    // ---- bounded planar carriers meeting a quadric: the drilled side wall ----

    // A wall in the plane y = -2.5 spanning x ∈ [-5, 5], z ∈ [1, 2.5].
    private static ExtrudedSurface Wall() => PocketWall();

    [Fact]
    public void BoreThroughAnExtrudedWall_RimIsTheExactCircle()
    {
        // THE regression this path exists for. A bore drilled into an extruded SIDE face
        // used to get a fixed ~57-sample tracer polyline for its rim while the identical
        // bore on a flat cap got a Circle3d — a volume-error floor no tessellation
        // density could lower. The wall IS a plane; the rim IS that plane's circle.
        var wall = Wall();
        var bore = new CylinderSurface((0, 0, 1.75), Vector3d.UnitX, Vector3d.UnitZ, 0.3);

        var circle = Assert.IsType<Circle3d>(
            Assert.Single(SurfaceIntersection.Intersect(wall, bore, PlateRegion)));
        Assert.Equal(0, circle.Center.DistanceTo((0, -2.5, 1.75)), 12);
        Assert.Equal(0.3, circle.Radius, 12);
        // Phase alignment survives: the circle keeps the CYLINDER's own frame, so the
        // bore band's grid and this rim sample identical points.
        Assert.Equal(0, circle.PointAt(0).DistanceTo((0.3, -2.5, 1.75)), 12);
    }

    [Fact]
    public void BoreThroughAnExtrudedWall_RevolvedToolWallAlsoResolvesExactly()
    {
        // Drill tools are axis-touching REVOLVES, not extruded circles, so the revolved
        // carrier is the case that actually fires in the Shape pipeline.
        var wall = Wall();
        var bore = new RevolvedSurface(
            new Line3d((0.3, -2.5, 1.75), (0.3, -1.0, 1.75)), (0, -2.5, 1.75), Vector3d.UnitY,
            2 * Math.PI);

        var circle = Assert.IsType<Circle3d>(
            Assert.Single(SurfaceIntersection.Intersect(wall, bore, PlateRegion)));
        Assert.Equal(0, circle.Center.DistanceTo((0, -2.5, 1.75)), 12);
        Assert.Equal(0.3, circle.Radius, 12);
    }

    /// <summary>
    /// The guard the <c>Promote</c> trap earns: "lies on the carrier" is not "IS the
    /// carrier", so a clipped arc must be checked GEOMETRICALLY — every sample on the
    /// extrusion's own parameter rectangle, never a type test.
    /// </summary>
    private static (double OffSurface, double OutOfDomain) OnPatch(Curve3d curve, ExtrudedSurface wall)
    {
        double offSurface = 0, outOfDomain = 0;
        var domain = curve.Domain;
        for (int i = 0; i <= 200; i++)
        {
            var p = curve.PointAt(domain.ParameterAt(i / 200.0));
            if (!wall.TryProjectPoint(p, out var uv, FaceGeometry.InverseEvaluationTolerance))
                return (double.PositiveInfinity, double.PositiveInfinity);
            offSurface = Math.Max(offSurface, p.DistanceTo(wall.PointAt(uv.X, uv.Y)));
            outOfDomain = Math.Max(outOfDomain,
                Math.Max(Math.Max(-uv.X, uv.X - 1), Math.Max(-uv.Y, uv.Y - 1)));
        }
        return (offSurface, outOfDomain);
    }

    [Fact]
    public void BoreCrossingTheWallsTopEdge_ClipsToTheExactArc()
    {
        // The wall spans z ∈ [1, 2.5]; this bore's rim runs off the top. The analytic
        // conic is real — the wall IS that plane — but only the run below z = 2.5 is
        // surface the wall carries, so the answer is an ARC of the exact circle, not the
        // whole circle (which would report geometry the face does not have) and not the
        // tracer's chordal polyline (a fixed-sample floor).
        var wall = Wall();
        var bore = new CylinderSurface((0, 0, 2.4), Vector3d.UnitX, Vector3d.UnitZ, 0.3);

        var arc = Assert.Single(SurfaceIntersection.Intersect(wall, bore, PlateRegion));
        var segment = Assert.IsType<CurveSegment>(arc);
        Assert.IsType<Circle3d>(segment.Base);

        // Both ends land ON the wall's top edge, and at the x the TOP face's own
        // intersection with the same cylinder reaches: ±√(r² − (2.5 − 2.4)²).
        double xExact = Math.Sqrt(0.3 * 0.3 - 0.1 * 0.1);
        foreach (double t in (double[])[arc.Domain.Start, arc.Domain.End])
        {
            var end = arc.PointAt(t);
            Assert.Equal(2.5, end.Z, 12);
            Assert.Equal(xExact, Math.Abs(end.X), 12);
        }
        // The major arc survives: it dips to the bottom of the circle at z = 2.1.
        Assert.Equal(2.1, arc.PointAt(arc.Domain.ParameterAt(0.5)).Z, 12);
    }

    [Fact]
    public void ClippedArc_LiesEntirelyOnTheWallItIsReportedFor()
    {
        // The Promote lesson, as a measurement: a curve reported for a bounded carrier
        // must be surface that carrier actually has. Sampled against the extrusion's own
        // (u, v), the arc never escapes [0, 1]².
        var wall = Wall();
        var bore = new CylinderSurface((0, 0, 2.4), Vector3d.UnitX, Vector3d.UnitZ, 0.3);

        var arc = Assert.Single(SurfaceIntersection.Intersect(wall, bore, PlateRegion));
        var (offSurface, outOfDomain) = OnPatch(arc, wall);
        Assert.True(offSurface < 1e-12, $"off the wall by {offSurface}");
        Assert.True(outOfDomain <= 0, $"escaped the wall's parameter rectangle by {outOfDomain}");
    }

    [Fact]
    public void BoreCrossingTheWallsEndEdge_ClipsAgainstTheOtherPatchDirection()
    {
        // The same clip against the generator's own extent rather than the extrusion's:
        // a bore drilled at the very end of a pocket wall.
        var wall = Wall();
        var bore = new CylinderSurface((4.9, 0, 1.75), Vector3d.UnitX, Vector3d.UnitZ, 0.3);

        var arc = Assert.Single(SurfaceIntersection.Intersect(wall, bore, PlateRegion));
        double zExact = Math.Sqrt(0.3 * 0.3 - 0.1 * 0.1);
        foreach (double t in (double[])[arc.Domain.Start, arc.Domain.End])
        {
            var end = arc.PointAt(t);
            Assert.Equal(5, end.X, 12);
            Assert.Equal(zExact, Math.Abs(end.Z - 1.75), 12);
        }
        Assert.Equal(4.6, arc.PointAt(arc.Domain.ParameterAt(0.5)).X, 12);
    }

    [Fact]
    public void BoreTallerThanTheWall_ClipsToTwoArcs()
    {
        // A bore wider than the wall is deep leaves TWO runs, one either side — which is
        // why membership is decided per interval rather than by an inequality on the
        // crossing list.
        var wall = Wall(); // z ∈ [1, 2.5]
        var bore = new CylinderSurface((0, 0, 1.75), Vector3d.UnitX, Vector3d.UnitZ, 1.0);

        var arcs = SurfaceIntersection.Intersect(wall, bore, PlateRegion);
        Assert.Equal(2, arcs.Count);
        double xExact = Math.Sqrt(1.0 - 0.75 * 0.75);
        foreach (var arc in arcs)
        {
            var (offSurface, outOfDomain) = OnPatch(arc, wall);
            Assert.True(offSurface < 1e-12 && outOfDomain <= 0);
            foreach (double t in (double[])[arc.Domain.Start, arc.Domain.End])
            {
                var end = arc.PointAt(t);
                Assert.Equal(xExact, Math.Abs(end.X), 12);
                Assert.Equal(1.5, Math.Abs(end.Z - 1.75) * 2, 12); // z = 1 or z = 2.5
            }
        }
        // One of them straddles the circle's own seam (θ = 0 sits at +x, inside the wall),
        // so it must come back as ONE segment running past the domain end rather than two.
        Assert.Contains(arcs, a => a is CurveSegment { BaseEnd: > 2 * Math.PI });
    }

    [Fact]
    public void BoresTangentToTheWallsTopEdge_StayClosedCircles()
    {
        // A bore whose rim TOUCHES the wall's top edge without crossing it. The two roots
        // of that edge's equation coincide mathematically, and acos's square-root
        // conditioning cannot resolve them — they come back ~1e-7 rad apart however exact
        // the geometry is, and the midpoint between them reads inside or outside by
        // round-off. So the answer is decided by the short-run rule: dropping a run of span
        // δ removes a chord of scale·δ (an outright gap) while keeping it leaves the curve
        // only scale·(1 − cos(δ/2)) outside the patch, second order in δ — so the run is
        // kept and the conic stays CLOSED.
        //
        // <para><b>Whether the round-off falls the safe way is ALIGNMENT, not tolerance</b>,
        // so the instrument is the family rather than one fixture: measured, 62 of these
        // 480 configurations come back as an arc with a pinhole in it without the rule, and
        // 0 of 480 with it. Both seam alignments are swept, because a run straddling θ = 0
        // exercises the cyclic span arithmetic that a run in the middle does not.</para>
        int checkedCases = 0;
        foreach (double height in (double[])[1.5, 1.25, 1.0, 2.0, 0.9, 1.7])
        {
            var wall = new ExtrudedSurface(new Line3d((-5, -2.5, 1), (5, -2.5, 1)), (0, 0, height));
            for (int i = 0; i < 40; i++)
            {
                // Strictly under height/2, so the top edge is the ONLY contact.
                double r = 0.02 + i * (height / 2 - 0.04) / 40;
                double z0 = 1 + height - r;
                foreach (var (x, y) in ((Vector3d, Vector3d)[])
                    [(Vector3d.UnitZ, Vector3d.UnitX),   // θ = 0 AT the tangency
                     (Vector3d.UnitX, Vector3d.UnitZ)])  // θ = 0 a quarter turn away
                {
                    var bore = new CylinderSurface((0, 0, z0), x, y, r);
                    var curves = SurfaceIntersection.Intersect(wall, bore, PlateRegion);
                    Assert.IsType<Circle3d>(Assert.Single(curves));
                    checkedCases++;
                }
            }
        }
        Assert.Equal(480, checkedCases);
    }

    [Fact]
    public void ObliqueBoreCrossingTheWallsEdge_ClipsTheEllipse()
    {
        // The conic need not be a circle: a tilted bore sections the wall in an ellipse,
        // and the clip is the same closed-form harmonic solve.
        var wall = Wall();
        var axis = new Vector3d(0, 2, 1).Normalized();
        var x = axis.ArbitraryPerpendicular(Tolerance.Default);
        // Origin ON the wall plane, so the ellipse is centred at z = 2.4 and its only
        // escape is over the top edge.
        var bore = new CylinderSurface((0, -2.5, 2.4), x, axis.Cross(x), 0.3);

        var arc = Assert.Single(SurfaceIntersection.Intersect(wall, bore, PlateRegion));
        Assert.IsType<Ellipse3d>(Assert.IsType<CurveSegment>(arc).Base);
        var (offSurface, outOfDomain) = OnPatch(arc, wall);
        Assert.True(offSurface < 1e-12 && outOfDomain <= 0);
        foreach (double t in (double[])[arc.Domain.Start, arc.Domain.End])
            Assert.Equal(2.5, arc.PointAt(t).Z, 12);
    }

    [Fact]
    public void SphericalCavityCrossingTheWallsEdge_ClipsTheCircle()
    {
        var wall = Wall();
        var cavity = new SphereSurface((0, -2.5, 2.4), 0.3);

        var arc = Assert.Single(SurfaceIntersection.Intersect(wall, cavity, PlateRegion));
        Assert.IsType<Circle3d>(Assert.IsType<CurveSegment>(arc).Base);
        foreach (double t in (double[])[arc.Domain.Start, arc.Domain.End])
            Assert.Equal(2.5, arc.PointAt(t).Z, 12);
    }

    [Fact]
    public void BoreParallelToTheWall_StillDefersToTheTracer()
    {
        // The axis-parallel LINE pair is not a conic, so the clip has nothing to say and
        // the pair keeps its incumbent route. Pinned so the tier's boundary is a decision
        // rather than an accident.
        var wall = Wall();
        var bore = new CylinderSurface((0, -2.5, 0), Vector3d.UnitX, Vector3d.UnitY, 0.3);

        Assert.All(SurfaceIntersection.Intersect(wall, bore, PlateRegion),
            c => Assert.IsType<PolylineCurve3d>(c.Underlying));
    }

    [Fact]
    public void BoreAboveTheWall_NoCurve()
    {
        // The plane the wall lies in still meets the cylinder, but the wall itself does
        // not: the exact (s, t) range test rejects the circle, and the tracer finds
        // nothing on the bounded surface either.
        var wall = Wall();
        var bore = new CylinderSurface((0, 0, 5), Vector3d.UnitX, Vector3d.UnitZ, 0.3);
        Assert.Empty(SurfaceIntersection.Intersect(wall, bore, PlateRegion));
    }

    [Fact]
    public void UnboundedPlaneMeetingACylinder_KeepsTheOriginalPath()
    {
        // The bounded-patch branch must never capture a real PlaneSurface: the boolean
        // pipeline's whole regression surface runs through the main switch.
        var plane = new PlaneSurface((0, -2.5, 0), Vector3d.UnitX, Vector3d.UnitZ);
        var bore = new CylinderSurface((0, 0, 1.75), Vector3d.UnitX, Vector3d.UnitZ, 0.3);

        var circle = Assert.IsType<Circle3d>(
            Assert.Single(SurfaceIntersection.Intersect(plane, bore, PlateRegion)));
        Assert.Equal(0.3, circle.Radius, 12);
    }

    [Fact]
    public void ExtrudedCurvedGenerator_StillMarches()
    {
        // Only STRAIGHT generators become bounded planar patches; a genuinely curved one
        // (that is not a promotable full circle) must keep the general path.
        var arc = NurbsCurve.Arc((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 2, 0, Math.PI);
        var wall = new ExtrudedSurface(arc, (0, 0, 4));
        var side = new PlaneSurface((1, 0, 0), Vector3d.UnitY, Vector3d.UnitZ); // x = 1, not ⊥ the direction

        var curves = SurfaceIntersection.Intersect(side, wall, Region);
        Assert.NotEmpty(curves);
        Assert.All(curves, c => Assert.IsType<PolylineCurve3d>(c));
    }

    /// <summary>A rounded rectangle's corner: a quarter arc of a circle, extruded.</summary>
    private static ExtrudedSurface QuarterArcCorner() => new(
        new CurveSegment(new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 6), 0, Math.PI / 2),
        (0, 0, 10));

    [Fact]
    public void QuarterArcExtrusion_IsNotPromotedToAWholeCylinder()
    {
        // Every point of a quarter arc lies on the full cylinder, so a start-point-only
        // promotion guard accepts it — and then reports 270 degrees of surface the face
        // does not carry. The section here must stay the ARC: same start, and a midpoint
        // at 45 degrees rather than the full circle's 180.
        var corner = QuarterArcCorner();
        var cap = new PlaneSurface((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);

        var curve = Assert.Single(SurfaceIntersection.Intersect(corner, cap, new Aabb((-20, -20, -20), (20, 20, 20))));
        Assert.False(curve.IsClosed);
        Assert.Equal(0, curve.PointAt(curve.Domain.Start).DistanceTo((6, 0, 5)), 9);
        Assert.Equal(0, curve.PointAt(curve.Domain.End).DistanceTo((0, 6, 5)), 9);
        double diagonal = 6 / Math.Sqrt(2);
        Assert.Equal(0, curve.PointAt(curve.Domain.Mid).DistanceTo((diagonal, diagonal, 5)), 9);
    }

    [Fact]
    public void FullCircleExtrusion_IsStillPromotedToACylinder()
    {
        // The promotion exists for bore walls, which sweep the whole circle; a section is
        // then the exact closed Circle3d, not a tracer polyline.
        var bore = new ExtrudedSurface(
            new CurveSegment(new Circle3d((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 6), 0, 2 * Math.PI),
            (0, 0, 10));
        var cap = new PlaneSurface((0, 0, 5), Vector3d.UnitX, Vector3d.UnitY);

        var circle = Assert.IsType<Circle3d>(
            Assert.Single(SurfaceIntersection.Intersect(bore, cap, new Aabb((-20, -20, -20), (20, 20, 20)))));
        Assert.Equal(6, circle.Radius, 12);
    }

    [Fact]
    public void QuarterArcCorner_ReportsNothingAgainstACylinderItNeverReaches()
    {
        // The counterbore-near-a-rounded-corner near miss, at the surface layer: a radius-4
        // cylinder whose axis sits 4*sqrt(2) from the corner centre crosses the corner's
        // CARRIER circle at 186 and 264 degrees — nowhere near the [0, 90] quarter the
        // corner actually covers.
        var corner = QuarterArcCorner();
        var tool = new CylinderSurface(
            (-4, -4, 0), Vector3d.UnitX, Vector3d.UnitY, 4);

        Assert.Empty(SurfaceIntersection.Intersect(corner, tool, new Aabb((-20, -20, -5), (20, 20, 15))));
    }
}
