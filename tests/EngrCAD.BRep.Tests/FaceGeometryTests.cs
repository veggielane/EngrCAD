using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class FaceGeometryTests
{
    [Fact]
    public void TryProjectPoint_AnalyticSurfaces_RoundTrip()
    {
        var cylinder = new CylinderSurface((1, 2, 3), Vector3d.UnitX, Vector3d.UnitY, 1.5);
        var sphere = new SphereSurface((0, 0, 0), 2.0);
        var plane = new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);

        var rng = new Random(41);
        for (int i = 0; i < 30; i++)
        {
            double u = rng.NextDouble() * 2 * Math.PI;
            double v = rng.NextDouble() * 4 - 2;

            var pc = cylinder.PointAt(u, v);
            Assert.True(cylinder.TryProjectPoint(pc, out var uvc));
            Assert.True(cylinder.PointAt(uvc.X, uvc.Y).AreEqual(pc, new Tolerance(1e-9, 1e-9)));

            double lat = (rng.NextDouble() - 0.5) * 3; // stay off the exact poles
            var ps = sphere.PointAt(u, lat / 2);
            Assert.True(sphere.TryProjectPoint(ps, out var uvs));
            Assert.True(sphere.PointAt(uvs.X, uvs.Y).AreEqual(ps, new Tolerance(1e-9, 1e-9)));

            var pp = plane.PointAt(v, u);
            Assert.True(plane.TryProjectPoint(pp, out var uvp));
            Assert.True(uvp.AreEqual(new Vector2d(v, u), new Tolerance(1e-9, 1e-9)));
        }

        // Off-surface points are rejected.
        Assert.False(cylinder.TryProjectPoint((1, 2, 3), out _));       // on the axis
        Assert.False(sphere.TryProjectPoint((3, 0, 0), out _));
        Assert.False(plane.TryProjectPoint((0, 0, 2), out _));
    }

    [Fact]
    public void TryProjectPoint_Newton_OnNurbsPatch()
    {
        var patch = new NurbsSurface(
            2, 2,
            new Vector3d[3, 3]
            {
                { (0, 0, 0), (0, 1, 0.5), (0, 2, 0) },
                { (1, 0, 0.5), (1, 1, 1.5), (1, 2, 0.5) },
                { (2, 0, 0), (2, 1, 0.5), (2, 2, 0) },
            },
            null,
            [0, 0, 0, 1, 1, 1], [0, 0, 0, 1, 1, 1]);

        var target = patch.PointAt(0.37, 0.62);
        Assert.True(patch.TryProjectPoint(target, out var uv));
        Assert.True(patch.PointAt(uv.X, uv.Y).AreEqual(target, new Tolerance(1e-7, 1e-7)));
    }

    [Fact]
    public void PullCurve_TiltedEllipseOntoCylinder_IsContinuousSinusoid()
    {
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);
        double tilt = Math.PI / 6;
        var normal = new Vector3d(Math.Sin(tilt), 0, Math.Cos(tilt));
        var x = normal.ArbitraryPerpendicular(Tolerance.Default);
        var plane = new PlaneSurface((0, 0, 0), x, normal.Cross(x));
        var ellipse = Assert.Single(SurfaceIntersection.Intersect(plane, cylinder,
            new Aabb((-3, -3, -3), (3, 3, 3))));

        var pulled = FaceGeometry.PullCurve(ellipse, cylinder, samples: 96);

        // Continuous in u (no 2π jumps) and spanning one full period.
        for (int i = 1; i < pulled.Count; i++)
            Assert.True(Math.Abs(pulled[i].X - pulled[i - 1].X) < 0.5, $"u jump at {i}");
        double span = pulled.Max(p => p.X) - pulled.Min(p => p.X);
        Assert.True(Math.Abs(span - 2 * Math.PI) < 0.2, $"u span {span}");

        // v follows the plane: v = -tan(tilt) · cos(u-ish) — verify samples re-evaluate onto the curve.
        for (int i = 0; i < pulled.Count; i++)
        {
            var p = cylinder.PointAt(pulled[i].X, pulled[i].Y);
            Assert.True(Math.Abs((p - plane.Origin).Dot(plane.Normal)) < 1e-6, "pulled point off plane");
        }
    }

    [Fact]
    public void Contains_PlanarFaceAndCylinderBand()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 1)));
        var top = box.Faces.First(f => f.Surface is PlaneSurface p && p.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));

        Assert.True(FaceGeometry.Contains(top, (1, 1, 1)));
        Assert.True(FaceGeometry.Contains(top, (0.1, 1.7, 1)));
        Assert.False(FaceGeometry.Contains(top, (2.5, 1, 1)));  // beyond the face
        Assert.False(FaceGeometry.Contains(top, (1, 1, 0.5)));  // not on the surface

        var cylinder = SolidFactory.MakeCylinder(1.0, 2.0);
        var band = cylinder.Faces.First(f => f.Surface is CylinderSurface);
        Assert.True(FaceGeometry.Contains(band, (1, 0, 1)));
        Assert.True(FaceGeometry.Contains(band, (0, -1, 0.2)));
        Assert.False(FaceGeometry.Contains(band, (1, 0, 2.5))); // beyond the top cap
        Assert.False(FaceGeometry.Contains(band, (0.5, 0, 1))); // interior, not on the surface
    }

    [Fact]
    public void SplitByClosedCurve_ProducesManifoldHoleAndDisk()
    {
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));
        var top = box.Faces.First(f => f.Surface is PlaneSurface p && p.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));
        var bore = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 0.5);
        var circle = Assert.Single(SurfaceIntersection.Intersect(top.Surface, bore,
            new Aabb((-2, -2, -1), (2, 2, 2))));

        var split = FaceSplitter.SplitByClosedCurve(top, circle);

        Assert.Equal(2, split.FaceWithHole.Loops.Count);
        Assert.NotNull(split.Disk);
        Assert.Equal(2, split.Edge.Uses.Count);
        Assert.NotEqual(split.Edge.Uses[0].SameSense, split.Edge.Uses[1].SameSense);

        // Containment flips across the circle.
        Assert.True(FaceGeometry.Contains(split.FaceWithHole, (0.8, 0.8, 1)));
        Assert.False(FaceGeometry.Contains(split.FaceWithHole, (0, 0, 1)));
        Assert.True(FaceGeometry.Contains(split.Disk, (0, 0, 1)));
        Assert.False(FaceGeometry.Contains(split.Disk, (0.8, 0.8, 1)));

        // A curve outside the face is rejected.
        var farBore = new CylinderSurface((5, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 0.5);
        var farCircle = Assert.Single(SurfaceIntersection.Intersect(
            new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY), farBore,
            new Aabb((3, -2, -1), (7, 2, 2))));
        Assert.Throws<ArgumentException>(() => FaceSplitter.SplitByClosedCurve(split.FaceWithHole, farCircle));
    }

    /// <summary>
    /// Locks the epsilon ladder's two named B-Rep tiers to their documented values.
    /// These are boolean-critical: <c>SealSeams</c>, the tessellator's full-domain
    /// boundary match and <c>Profile</c>'s chain join all key on the seam tier, while
    /// every pullback call site keys on the inverse-evaluation tier. A "cleanup" that
    /// tightens either toward the 1e-9 weld tolerance silently unwelds boolean output,
    /// so the values are asserted exactly rather than tolerantly.
    /// </summary>
    [Fact]
    public void EpsilonLadder_NamedTiers_HoldTheirDocumentedValues()
    {
        Assert.Equal(1e-7, FaceGeometry.SeamTolerance);
        Assert.Equal(1e-6, FaceGeometry.InverseEvaluationTolerance);

        // Ordering is the invariant that matters: weld < seam < inverse evaluation.
        Assert.True(Tolerance.Default.Linear < FaceGeometry.SeamTolerance);
        Assert.True(FaceGeometry.SeamTolerance < FaceGeometry.InverseEvaluationTolerance);
    }

    // ---- the polyline-sampling rule ----

    /// <summary>A quarter of a unit circle as the tracer would deliver it: exact at its
    /// vertices, a chord (and a sagitta off the circle) between them.</summary>
    private static PolylineCurve3d ArcPolyline(int segments = 8)
    {
        var points = new List<Vector3d>();
        for (int i = 0; i <= segments; i++)
        {
            double a = Math.PI / 2 * i / segments;
            points.Add(new Vector3d(Math.Cos(a), Math.Sin(a), 0));
        }
        return new PolylineCurve3d(points);
    }

    [Fact]
    public void IsPolylineBacked_SeesThroughACurveSegment()
    {
        var polyline = ArcPolyline();
        Assert.True(FaceGeometry.IsPolylineBacked(polyline));
        Assert.True(FaceGeometry.IsPolylineBacked(new CurveSegment(polyline, 0.25, 0.75)));
        // A segment of an ANALYTIC curve is exact everywhere and must not be captured;
        // note its Underlying is the circle, which is why the test cannot be on that.
        var arc = new CurveSegment(new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1), 0, 1);
        Assert.False(FaceGeometry.IsPolylineBacked(arc));
        Assert.False(FaceGeometry.IsPolylineBacked(new Line3d((0, 0, 0), (1, 0, 0))));
    }

    [Fact]
    public void ExactSampleParameters_OfASegmentLandOnTheBasesVertices()
    {
        // THE case this rule exists for: after a cut, the face splitter hands back a
        // CurveSegment WRAPPING the traced polyline. Sampling it uniformly puts every
        // interior sample mid-chord — a sagitta off the carrier surface — so pullback
        // and tessellation silently disagree about where the edge is.
        var polyline = ArcPolyline();
        var segment = new CurveSegment(polyline, polyline.Domain.ParameterAt(0.25), polyline.Domain.ParameterAt(0.75));

        var parameters = FaceGeometry.ExactSampleParameters(segment, 0, 1, uniformSamples: 24);

        Assert.Equal(0, parameters[0], 12);
        Assert.Equal(1, parameters[^1], 12);
        Assert.True(parameters.Count > 2, "the half-arc spans interior vertices");
        Assert.Equal(parameters.OrderBy(t => t), parameters); // ascending

        // Every sample is exactly on the unit circle, which the polyline touches only at
        // its vertices — the property that makes the rule load-bearing.
        foreach (double t in parameters)
            Assert.Equal(1.0, segment.PointAt(t).Length, 12);

        // And a uniform sampling of the same segment is NOT: the check can see the bug.
        double worst = 0;
        for (int i = 1; i < 24; i++)
            worst = Math.Max(worst, Math.Abs(segment.PointAt(i / 24.0).Length - 1));
        Assert.True(worst > 1e-4, $"uniform samples should sit off the circle, worst {worst:E3}");
    }

    [Fact]
    public void ExactSampleParameters_OfAnAnalyticCurveStayUniform()
    {
        var line = new Line3d((0, 0, 0), (3, 0, 0));
        var parameters = FaceGeometry.ExactSampleParameters(line, 0, 1, uniformSamples: 4);
        Assert.Equal([0, 0.25, 0.5, 0.75, 1], parameters.Select(t => Math.Round(t, 12)));
    }
}
