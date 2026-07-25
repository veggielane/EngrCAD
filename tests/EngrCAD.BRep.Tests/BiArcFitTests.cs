using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// Biarc fitting: the single fit, the tolerance-driven chain, and the honest error
/// metric a caller uses to decide whether the approximation is acceptable.
/// </summary>
public class BiArcFitTests
{
    private static Vector2d OnCircle(in Vector2d center, double radius, double angle) =>
        center + new Vector2d(Math.Cos(angle), Math.Sin(angle)) * radius;

    private static Vector2d CircleTangent(double angle) => new(-Math.Sin(angle), Math.Cos(angle));

    // ------------------------------------------------------------- the single fit

    [Fact]
    public void Fit_ReproducesEndPointsAndTangents()
    {
        var p1 = new Vector2d(0, 0);
        var t1 = new Vector2d(1, 0);
        var p2 = new Vector2d(10, 6);
        var t2 = new Vector2d(0, 1);

        var biarc = BiArcFit.Fit(p1, t1, p2, t2);
        var tight = new Tolerance(1e-13, 1e-13);
        Assert.True(biarc.First.PointAt(0).AreEqual(p1, tight));
        Assert.True(biarc.Second.PointAt(1).AreEqual(p2, tight));
        Assert.True(biarc.First.TangentAt(0).AreEqual(t1, tight));
        Assert.True(biarc.Second.TangentAt(1).AreEqual(t2, tight));
    }

    [Fact]
    public void Fit_IsTangentContinuousAtTheJoint()
    {
        var biarc = BiArcFit.Fit((0, 0), (1, 0.3), (12, -4), (0.5, -1));
        var tight = new Tolerance(1e-12, 1e-12);
        Assert.True(biarc.First.PointAt(1).AreEqual(biarc.Second.PointAt(0), tight));
        Assert.True(biarc.First.PointAt(1).AreEqual(biarc.Joint, tight));
        Assert.True(biarc.First.TangentAt(1).AreEqual(biarc.Second.TangentAt(0), tight));
    }

    [Fact]
    public void Fit_OnCircleDataReproducesTheCircleExactly()
    {
        // Two points on a circle with the circle's own tangents: both arcs of the
        // equal-distance biarc must BE that circle. This is the analytic ground truth
        // the whole method rests on.
        var center = new Vector2d(3, -2);
        const double radius = 5;
        double a0 = 0.3, a1 = 1.7;
        var biarc = BiArcFit.Fit(OnCircle(center, radius, a0), CircleTangent(a0),
                                 OnCircle(center, radius, a1), CircleTangent(a1));

        foreach (var piece in biarc.Curves)
        {
            var arc = Assert.IsType<Arc2d>(piece);
            Assert.Equal(radius, arc.Radius, 10);
            Assert.True(arc.Center.AreEqual(center, new Tolerance(1e-10, 1e-10)));
            Assert.True(arc.SweepAngle > 0); // both traverse counter-clockwise
        }
        // Total sweep equals the data span.
        double sweep = ((Arc2d)biarc.First).SweepAngle + ((Arc2d)biarc.Second).SweepAngle;
        Assert.Equal(a1 - a0, sweep, 10);
    }

    [Fact]
    public void Fit_CollinearDataDegeneratesToStraightSegments()
    {
        var biarc = BiArcFit.Fit((0, 0), (1, 0), (10, 0), (1, 0));
        Assert.IsType<Line2d>(biarc.First);
        Assert.IsType<Line2d>(biarc.Second);
        Assert.Equal(10, biarc.Length, 12);
    }

    [Fact]
    public void Fit_ParallelTangentsAreStableWithoutABranchTest()
    {
        // The stable form d = |v|²/(√disc + v·t) must stay accurate as the tangents
        // become parallel — the g3 original switched formulas at an arbitrary epsilon on
        // |t1+t2|², a SQUARED quantity, so the two branches disagreed near the boundary.
        var p1 = new Vector2d(0, 0);
        var p2 = new Vector2d(10, 1);
        double previous = double.NaN;
        foreach (double wobble in new[] { 1e-2, 1e-4, 1e-6, 1e-8, 1e-10, 1e-12, 0.0 })
        {
            var t1 = new Vector2d(1, 0);
            var t2 = new Vector2d(Math.Cos(wobble), Math.Sin(wobble));
            Assert.Equal(BiArcFitStatus.Success, BiArcFit.TryFit(p1, t1, p2, t2, out var biarc));
            double d = biarc!.D1;
            if (!double.IsNaN(previous))
                Assert.True(Math.Abs(d - previous) < 1e-2, $"d jumped from {previous} to {d} at wobble {wobble}");
            previous = d;
            // And the fit is still a fit.
            Assert.True(biarc.First.PointAt(0).AreEqual(p1, new Tolerance(1e-12, 1e-12)));
            Assert.True(biarc.Second.PointAt(1).AreEqual(p2, new Tolerance(1e-12, 1e-12)));
        }
    }

    [Fact]
    public void Fit_SemicircleUTurnProducesTwoRealArcs()
    {
        // Equal tangents perpendicular to the chord: the classic U-turn. The g3 port
        // assigns its first arc twice here and leaves the second uninitialized.
        var p1 = new Vector2d(0, 0);
        var p2 = new Vector2d(0, 8);
        var t = new Vector2d(1, 0);
        Assert.Equal(BiArcFitStatus.Success, BiArcFit.TryFit(p1, t, p2, t, out var biarc));

        var first = Assert.IsType<Arc2d>(biarc!.First);
        var second = Assert.IsType<Arc2d>(biarc.Second);
        Assert.Equal(2, first.Radius, 10);
        Assert.Equal(2, second.Radius, 10);
        Assert.Equal(Math.PI, Math.Abs(first.SweepAngle), 10);
        Assert.Equal(Math.PI, Math.Abs(second.SweepAngle), 10);
        Assert.True(biarc.Second.PointAt(1).AreEqual(p2, new Tolerance(1e-12, 1e-12)));
    }

    [Fact]
    public void TryFit_RejectsDegenerateInput()
    {
        Assert.Equal(BiArcFitStatus.CoincidentPoints,
            BiArcFit.TryFit((1, 1), (1, 0), (1, 1), (0, 1), out _));
        Assert.Equal(BiArcFitStatus.DegenerateTangent,
            BiArcFit.TryFit((0, 0), Vector2d.Zero, (1, 1), (0, 1), out _));
        // Parallel tangents pointing back along the chord: no finite biarc.
        Assert.Equal(BiArcFitStatus.NoFiniteSolution,
            BiArcFit.TryFit((0, 0), (-1, 0), (10, 0), (-1, 0), out _));
    }

    [Fact]
    public void TryFit_WithSpecifiedD1VariesTheArcSplit()
    {
        var p1 = new Vector2d(0, 0);
        var t1 = new Vector2d(1, 0);
        var p2 = new Vector2d(10, 5);
        var t2 = new Vector2d(0, 1);
        Assert.Equal(BiArcFitStatus.Success, BiArcFit.TryFit(p1, t1, p2, t2, out var balanced));

        Assert.Equal(BiArcFitStatus.Success, BiArcFit.TryFit(p1, t1, p2, t2, balanced!.D1 * 0.5, out var skewed));
        Assert.NotEqual(balanced.D1, skewed!.D1);
        // Still a valid biarc: endpoints, tangents, and joint continuity all hold.
        var tight = new Tolerance(1e-12, 1e-12);
        Assert.True(skewed.First.PointAt(0).AreEqual(p1, tight));
        Assert.True(skewed.Second.PointAt(1).AreEqual(p2, tight));
        Assert.True(skewed.First.PointAt(1).AreEqual(skewed.Second.PointAt(0), tight));
        Assert.True(skewed.First.TangentAt(1).AreEqual(skewed.Second.TangentAt(0), tight));
    }

    // ------------------------------------------------------------------ 2D chains

    [Fact]
    public void FitPolyline_OnCircleSamplesNeedsOneBiarcAndIsExact()
    {
        // Points sampled off a circle: the three-point tangent estimate is EXACT there,
        // so the whole span fits with a single biarc at round-off accuracy.
        var center = new Vector2d(-1, 4);
        const double radius = 12;
        var points = new List<Vector2d>();
        for (int i = 0; i <= 20; i++)
            points.Add(OnCircle(center, radius, 0.2 + 1.4 * i / 20.0));

        var chain = BiArcFit.FitPolyline(points, 1e-9);
        Assert.Equal(2, chain.Curves.Count);
        Assert.Equal(2, chain.ArcCount);
        Assert.True(chain.MaxDeviation < 1e-10, $"deviation {chain.MaxDeviation}");
        foreach (Arc2d arc in chain.Curves.Cast<Arc2d>())
            Assert.Equal(radius, arc.Radius, 8);
    }

    [Fact]
    public void FitPolyline_OnStraightSamplesGivesSegments()
    {
        var points = new List<Vector2d>();
        for (int i = 0; i <= 10; i++)
            points.Add(new Vector2d(i * 0.7, i * 0.7 * 2 + 1));
        var chain = BiArcFit.FitPolyline(points, 1e-9);
        Assert.Equal(2, chain.Curves.Count);
        Assert.Equal(2, chain.SegmentCount);
        Assert.True(chain.MaxDeviation < 1e-12);
    }

    [Theory]
    [InlineData(1e-2)]
    [InlineData(1e-4)]
    [InlineData(1e-6)]
    public void FitPolyline_HonoursTheRequestedTolerance(double tolerance)
    {
        // A cubic-ish sampled curve that is genuinely not circular anywhere.
        var points = new List<Vector2d>();
        for (int i = 0; i <= 200; i++)
        {
            double x = 10.0 * i / 200;
            points.Add(new Vector2d(x, Math.Sin(x) * 2 + 0.1 * x * x));
        }

        var chain = BiArcFit.FitPolyline(points, tolerance);
        Assert.True(chain.MaxDeviation <= tolerance,
            $"reported deviation {chain.MaxDeviation} exceeds tolerance {tolerance}");

        // Independently re-measure every sample against the whole chain: the reported
        // metric must not be optimistic.
        double worst = 0;
        foreach (var p in points)
        {
            double best = double.PositiveInfinity;
            foreach (var curve in chain.Curves)
                best = Math.Min(best, curve.DistanceTo(p));
            worst = Math.Max(worst, best);
        }
        Assert.True(worst <= chain.MaxDeviation + 1e-15,
            $"independent measurement {worst} beats the reported {chain.MaxDeviation}");
    }

    [Fact]
    public void FitPolyline_TighterToleranceUsesMorePieces()
    {
        var points = new List<Vector2d>();
        for (int i = 0; i <= 200; i++)
        {
            double x = 10.0 * i / 200;
            points.Add(new Vector2d(x, Math.Sin(x) * 2 + 0.1 * x * x));
        }
        int coarse = BiArcFit.FitPolyline(points, 1e-2).Curves.Count;
        int fine = BiArcFit.FitPolyline(points, 1e-6).Curves.Count;
        Assert.True(fine > coarse, $"fine={fine} coarse={coarse}");
    }

    [Fact]
    public void FitPolyline_ChainIsPositionContinuous()
    {
        var points = new List<Vector2d>();
        for (int i = 0; i <= 60; i++)
        {
            double t = i / 60.0 * 6;
            points.Add(new Vector2d(t, Math.Cos(t) * 3));
        }
        var chain = BiArcFit.FitPolyline(points, 1e-5);
        for (int i = 1; i < chain.Curves.Count; i++)
        {
            var end = chain.Curves[i - 1].PointAt(1);
            var start = chain.Curves[i].PointAt(0);
            Assert.True(end.AreEqual(start, new Tolerance(1e-9, 1e-9)),
                $"piece {i} starts at {start} but the previous ends at {end}");
        }
        Assert.True(chain.Curves[0].PointAt(0).AreEqual(points[0], new Tolerance(1e-12, 1e-12)));
        Assert.True(chain.Curves[^1].PointAt(1).AreEqual(points[^1], new Tolerance(1e-12, 1e-12)));
    }

    // ------------------------------------------------------------------ 3D chains

    [Fact]
    public void TryFitPolyline3d_OnAPlanarCircleGivesExactArcs()
    {
        // A circle in a tilted plane, sampled like a marching-tracer output.
        var frame = Frame3d.FromNormal((1, 2, 3), new Vector3d(1, 1, 1).Normalized());
        const double radius = 6;
        var points = new List<Vector3d>();
        for (int i = 0; i <= 24; i++)
        {
            double a = 0.1 + 2.0 * i / 24.0;
            points.Add(frame.ToWorld(new Vector3d(radius * Math.Cos(a), radius * Math.Sin(a), 0)));
        }

        Assert.Equal(BiArcFitStatus.Success, BiArcFit.TryFitPolyline(points, 1e-9, out var chain));
        Assert.Equal(2, chain.Curves.Count);
        Assert.Equal(2, chain.ArcCount);
        Assert.True(chain.MaxDeviation < 1e-10, $"deviation {chain.MaxDeviation}");
        Assert.True(chain.Planarity < 1e-12);

        // Every fitted point really is on the original circle.
        var center = frame.Origin;
        foreach (var curve in chain.Curves)
        {
            for (int i = 0; i <= 10; i++)
            {
                var p = curve.PointAt(curve.Domain.ParameterAt(i / 10.0));
                Assert.Equal(radius, (p - center).Length, 8);
            }
        }
    }

    [Fact]
    public void TryFitPolyline3d_RefusesNonPlanarInput()
    {
        var points = new List<Vector3d>();
        for (int i = 0; i <= 20; i++)
        {
            double a = i / 20.0 * 3;
            points.Add(new Vector3d(Math.Cos(a) * 4, Math.Sin(a) * 4, a)); // a helix
        }
        Assert.Equal(BiArcFitStatus.NotPlanar, BiArcFit.TryFitPolyline(points, 1e-6, out _));
    }

    [Fact]
    public void TryFitPolyline3d_ReportsTheTrueThreeDimensionalDeviation()
    {
        // A planar-to-1e-7 curve fitted at 1e-5: the metric must combine the flattening
        // residual with the in-plane fit error, never report only one of them.
        var points = new List<Vector3d>();
        var random = new Random(20260725);
        for (int i = 0; i <= 120; i++)
        {
            double x = i / 120.0 * 8;
            double wobble = (random.NextDouble() - 0.5) * 2e-7;
            points.Add(new Vector3d(x, Math.Sin(x) * 1.5, wobble));
        }

        Assert.Equal(BiArcFitStatus.Success, BiArcFit.TryFitPolyline(points, 1e-5, out var chain));
        Assert.True(chain.Planarity > 0);
        Assert.True(chain.MaxDeviation <= 1e-5, $"deviation {chain.MaxDeviation}");
        Assert.True(chain.MaxDeviation >= chain.Planarity,
            "the 3D deviation can never be smaller than the out-of-plane residual");

        // Independent check against the returned 3D geometry. Sampling a curve can only
        // OVER-estimate a distance, and by at most half the sample spacing (the foot can
        // fall midway between two samples), so that is the bound the assertion uses —
        // derived from the discretization, not a magic number.
        const int samples = 2000;
        double halfSpacing = 0;
        foreach (var curve in chain.Curves)
        {
            var previous = curve.PointAt(curve.Domain.Start);
            for (int i = 1; i <= samples; i++)
            {
                var next = curve.PointAt(curve.Domain.ParameterAt((double)i / samples));
                halfSpacing = Math.Max(halfSpacing, (next - previous).Length * 0.5);
                previous = next;
            }
        }

        double worst = 0;
        foreach (var p in points)
        {
            double best = double.PositiveInfinity;
            foreach (var curve in chain.Curves)
            {
                for (int i = 0; i <= samples; i++)
                    best = Math.Min(best, (curve.PointAt(curve.Domain.ParameterAt((double)i / samples)) - p).Length);
            }
            worst = Math.Max(worst, best);
        }
        Assert.True(worst <= chain.MaxDeviation + halfSpacing,
            $"sampled measurement {worst} exceeds the reported {chain.MaxDeviation} by more than the "
            + $"sampling bound {halfSpacing}");
    }

    [Fact]
    public void TryFitPolyline3d_ProducesStepExportableGeometry()
    {
        // Arcs come back as exact rational NURBS (what StepWriter emits as B_SPLINE_CURVE
        // with weights), straight runs as Line3d — never a sampled polyline.
        var points = new List<Vector3d>();
        for (int i = 0; i <= 30; i++)
        {
            double a = i / 30.0 * 1.2;
            points.Add(new Vector3d(Math.Cos(a) * 9, Math.Sin(a) * 9, 2));
        }
        Assert.Equal(BiArcFitStatus.Success, BiArcFit.TryFitPolyline(points, 1e-8, out var chain));
        foreach (var curve in chain.Curves)
            Assert.True(curve is NurbsCurve or Line3d or Circle3d, $"unexpected {curve.GetType().Name}");
    }
}
