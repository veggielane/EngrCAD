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
}
