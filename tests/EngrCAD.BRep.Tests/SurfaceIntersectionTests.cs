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
}
