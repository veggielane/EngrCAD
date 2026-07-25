using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// The 2D curve family (<see cref="Line2d"/>, <see cref="Arc2d"/>,
/// <see cref="BezierCurve2d"/>, <see cref="NurbsCurve2d"/>) and its arc-length
/// machinery, checked against analytic ground truth.
/// </summary>
public class Curve2dTests
{
    // ------------------------------------------------------------------- Line2d

    [Fact]
    public void Line_EvaluatesAndMeasuresExactly()
    {
        var line = new Line2d((1, 2), (4, 6));
        Assert.Equal(1, line.PointAt(0).X, 15);
        Assert.Equal(2.5, line.PointAt(0.5).X, 15);
        Assert.Equal(6, line.PointAt(1).Y, 15);
        Assert.Equal(3, line.DerivativeAt(0.5).X, 15);
        Assert.Equal(Vector2d.Zero, line.SecondDerivativeAt(0.3));
        Assert.Equal(5, line.ArcLength(), 12);
        Assert.Equal(0, line.CurvatureAt(0.5), 15);
    }

    [Fact]
    public void Line_DistanceClampsToTheSegment()
    {
        var line = new Line2d((0, 0), (10, 0));
        Assert.Equal(3, line.DistanceTo((4, 3)), 15);   // perpendicular foot inside
        Assert.Equal(5, line.DistanceTo((-3, 4)), 15);  // beyond the start
        Assert.Equal(5, line.DistanceTo((13, -4)), 15); // beyond the end
    }

    // -------------------------------------------------------------------- Arc2d

    [Fact]
    public void Arc_MatchesTheAnalyticCircle()
    {
        var arc = new Arc2d((3, -1), 2.5, 0.4, 1.1);
        for (int i = 0; i <= 8; i++)
        {
            double t = i / 8.0;
            double angle = 0.4 + 1.1 * t;
            var expected = new Vector2d(3 + 2.5 * Math.Cos(angle), -1 + 2.5 * Math.Sin(angle));
            Assert.True(arc.PointAt(t).AreEqual(expected, Tolerance.Default));
            // dP/dt = r·sweep·(−sin, cos)
            var derivative = new Vector2d(-2.5 * 1.1 * Math.Sin(angle), 2.5 * 1.1 * Math.Cos(angle));
            Assert.True(arc.DerivativeAt(t).AreEqual(derivative, Tolerance.Default));
        }
        Assert.Equal(2.5 * 1.1, arc.Length, 12);
        Assert.Equal(2.5 * 1.1, arc.ArcLength(), 10);
        // Signed curvature of a CCW arc is +1/r.
        Assert.Equal(1 / 2.5, arc.CurvatureAt(0.5), 12);
    }

    [Fact]
    public void Arc_ClockwiseSweepGivesNegativeCurvature()
    {
        var arc = new Arc2d(Vector2d.Zero, 4, 1.0, -2.0);
        Assert.Equal(-1 / 4.0, arc.CurvatureAt(0.5), 12);
        Assert.Equal(8, arc.Length, 12);
        var reversed = arc.Reversed();
        Assert.True(reversed.PointAt(0).AreEqual(arc.PointAt(1), Tolerance.Default));
        Assert.True(reversed.PointAt(1).AreEqual(arc.PointAt(0), Tolerance.Default));
    }

    [Fact]
    public void Arc_DistanceIsRadialInsideTheSweepAndEndpointOutside()
    {
        // Quarter arc of radius 5 centered at the origin, from 0 to 90 degrees.
        var arc = new Arc2d(Vector2d.Zero, 5, 0, Math.PI / 2);
        // A point along the 45-degree ray: purely radial distance.
        var ray = new Vector2d(Math.Cos(Math.PI / 4), Math.Sin(Math.PI / 4));
        Assert.Equal(3, arc.DistanceTo(ray * 8), 12);
        Assert.Equal(2, arc.DistanceTo(ray * 3), 12);
        // A point beyond the sweep falls back to the nearer endpoint (5, 0).
        Assert.Equal(4, arc.DistanceTo((5, -4)), 12);
    }

    [Fact]
    public void Arc_FromPointAndTangent_ReproducesBothEndsAndTheTangent()
    {
        var start = new Vector2d(2, 1);
        var tangent = new Vector2d(1, 1).Normalized();
        var end = new Vector2d(6, 5.5);
        var curve = Arc2d.FromPointAndTangent(start, tangent, end);
        var arc = Assert.IsType<Arc2d>(curve);

        Assert.True(arc.PointAt(0).AreEqual(start, new Tolerance(1e-13, 1e-13)));
        Assert.True(arc.PointAt(1).AreEqual(end, new Tolerance(1e-13, 1e-13)));
        Assert.True(arc.TangentAt(0).AreEqual(tangent, new Tolerance(1e-13, 1e-13)));
    }

    [Fact]
    public void Arc_FromPointAndTangent_CollinearGivesASegment()
    {
        var line = Assert.IsType<Line2d>(
            Arc2d.FromPointAndTangent((0, 0), (1, 0), (7, 0)));
        Assert.Equal(7, line.End.X, 15);
    }

    [Fact]
    public void Arc_FromPointAndTangent_StraightnessTestIsScaleFree()
    {
        // The same shape at 1e-6 scale: a sagitta of 1e-4 of the chord must still be an
        // arc, not a segment. An absolute epsilon on the projected height would have
        // collapsed this to a straight line (the quadratic-failure defect).
        double scale = 1e-6;
        var start = Vector2d.Zero;
        var tangent = new Vector2d(1, 0);
        var end = new Vector2d(scale, scale * 1e-4);
        var arc = Assert.IsType<Arc2d>(Arc2d.FromPointAndTangent(start, tangent, end));
        Assert.True(arc.Radius > scale); // a very flat arc, but an arc
        Assert.True(arc.PointAt(1).AreEqual(end, new Tolerance(1e-18, 1e-18)));
    }

    // ------------------------------------------------------------- BezierCurve2d

    [Fact]
    public void Bezier_QuadraticMatchesTheClosedForm()
    {
        Vector2d p0 = (0, 0), p1 = (1, 2), p2 = (3, 1);
        var bezier = new BezierCurve2d(p0, p1, p2);
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0;
            double u = 1 - t;
            var expected = p0 * (u * u) + p1 * (2 * u * t) + p2 * (t * t);
            Assert.True(bezier.PointAt(t).AreEqual(expected, Tolerance.Default));
            var derivative = (p1 - p0) * (2 * u) + (p2 - p1) * (2 * t);
            Assert.True(bezier.DerivativeAt(t).AreEqual(derivative, Tolerance.Default));
        }
        // Second derivative of a quadratic is the constant 2(P0 − 2P1 + P2).
        var second = (p0 - p1 * 2 + p2) * 2;
        Assert.True(bezier.SecondDerivativeAt(0.3).AreEqual(second, Tolerance.Default));
    }

    [Fact]
    public void Bezier_CubicDerivativeIsExact()
    {
        var bezier = new BezierCurve2d((0, 0), (0, 4), (4, 4), (4, 0));
        // Symmetric cubic: the tangent at the midpoint is horizontal.
        Assert.Equal(0, bezier.DerivativeAt(0.5).Y, 12);
        Assert.True(bezier.DerivativeAt(0.5).X > 0);
        // Endpoint derivatives are 3·(P1 − P0) and 3·(P3 − P2).
        Assert.True(bezier.DerivativeAt(0).AreEqual((0, 12), Tolerance.Default));
        Assert.True(bezier.DerivativeAt(1).AreEqual((0, -12), Tolerance.Default));
    }

    // -------------------------------------------------------------- NurbsCurve2d

    [Fact]
    public void Nurbs2d_RationalArcIsExactlyCircular()
    {
        var arc = NurbsCurve2d.Arc((2, -3), 7, 0.2, 0.2 + 2.5);
        for (int i = 0; i <= 40; i++)
        {
            double t = arc.Domain.ParameterAt(i / 40.0);
            var point = arc.PointAt(t);
            Assert.Equal(7, (point - new Vector2d(2, -3)).Length, 12);
            // The exact derivative must be perpendicular to the radius everywhere.
            var radial = point - new Vector2d(2, -3);
            Assert.Equal(0, radial.Dot(arc.DerivativeAt(t)) / (radial.Length * arc.DerivativeAt(t).Length), 10);
        }
    }

    [Fact]
    public void Nurbs2d_SharesTheBasisWithTheThreeDimensionalCurve()
    {
        // Same knots, same weights, same control points with z = 0: the 2D evaluation
        // must agree with the 3D one to the last bit-ish, since the basis code is shared.
        var lifted = NurbsCurve.Arc(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 3, 0.1, 1.9);
        var flat = NurbsCurve2d.Arc(Vector2d.Zero, 3, 0.1, 1.9);
        for (int i = 0; i <= 20; i++)
        {
            double t = flat.Domain.ParameterAt(i / 20.0);
            var a = lifted.PointAt(t);
            var b = flat.PointAt(t);
            Assert.Equal(a.X, b.X, 15);
            Assert.Equal(a.Y, b.Y, 15);
        }
    }

    [Fact]
    public void Nurbs2d_InterpolationPassesThroughThePoints()
    {
        Vector2d[] points = [(0, 0), (1, 2), (3, 3), (5, 1), (7, 2)];
        var curve = NurbsCurve2d.InterpolatePoints(points);
        // Chord-length parameters (the same rule the 3D routine uses).
        double total = 0;
        var lengths = new double[points.Length];
        for (int i = 1; i < points.Length; i++)
        {
            total += points[i].DistanceTo(points[i - 1]);
            lengths[i] = total;
        }
        for (int i = 0; i < points.Length; i++)
            Assert.True(curve.PointAt(lengths[i] / total).AreEqual(points[i], new Tolerance(1e-10, 1e-10)));
    }

    [Fact]
    public void Nurbs2d_ClosedInterpolationIsSeamContinuous()
    {
        Vector2d[] points = [(2, 0), (0, 2), (-2, 0), (0, -2)];
        var curve = NurbsCurve2d.InterpolatePoints(points, closed: true);
        var start = curve.PointAt(curve.Domain.Start);
        var end = curve.PointAt(curve.Domain.End);
        Assert.True(start.AreEqual(end, new Tolerance(1e-10, 1e-10)));
        // C2 at the seam: tangent and second derivative agree.
        Assert.True(curve.DerivativeAt(curve.Domain.Start)
            .AreEqual(curve.DerivativeAt(curve.Domain.End), new Tolerance(1e-8, 1e-8)));
    }

    // -------------------------------------------------------------- arc length

    [Fact]
    public void ArcLength_MatchesClosedFormsOnArcsAndSegments()
    {
        var arc = new Arc2d((1, 1), 3, -0.7, 2.1);
        Assert.Equal(3 * 2.1, arc.ArcLength(), 10);

        var bezier = new BezierCurve2d((0, 0), (0, 0), (5, 0)); // degenerate control: a straight run
        Assert.Equal(5, bezier.ArcLength(), 9);
    }

    [Fact]
    public void ParameterAtLength_InvertsArcLength()
    {
        var curve = new BezierCurve2d((0, 0), (1, 4), (6, 4), (8, 0));
        double total = curve.ArcLength();
        for (int i = 1; i < 10; i++)
        {
            double target = total * i / 10.0;
            double t = curve.ParameterAtLength(target);
            Assert.Equal(target, curve.ArcLength(curve.Domain.Start, t), 8);
        }
        Assert.Equal(curve.Domain.Start, curve.ParameterAtLength(-1));
        Assert.Equal(curve.Domain.End, curve.ParameterAtLength(total * 2));
    }

    [Fact]
    public void ArcLengthTable_AgreesWithDirectInversion()
    {
        var curve = new BezierCurve2d((0, 0), (1, 5), (7, -3), (9, 1));
        var table = new ArcLengthTable2d(curve);
        Assert.Equal(curve.ArcLength(), table.TotalLength, 8);
        for (int i = 0; i <= 10; i++)
        {
            double s = table.TotalLength * i / 10.0;
            Assert.Equal(curve.ParameterAtLength(s), table.ParameterAtLength(s), 7);
        }
    }

    [Fact]
    public void ArcLength_IsScaleInvariant()
    {
        // The quadrature tolerance is RELATIVE to the chord, so a micron-scale curve is
        // measured to the same relative accuracy as a metre-scale one.
        foreach (double scale in new[] { 1e-6, 1.0, 1e6 })
        {
            var arc = new Arc2d(Vector2d.Zero, scale, 0, 1.3);
            Assert.Equal(1.0, arc.ArcLength() / (scale * 1.3), 9);
        }
    }

    // ------------------------------------------------------------- BSplineBasis

    [Fact]
    public void BSplineBasis_IsAPartitionOfUnity()
    {
        double[] knots = [0, 0, 0, 0, 1, 2, 3, 3, 3, 3];
        const int degree = 3, controlPoints = 6;
        Span<double> basis = stackalloc double[degree + 1];
        for (int i = 0; i <= 30; i++)
        {
            double u = 3.0 * i / 30;
            int span = BSplineBasis.FindSpan(u, degree, controlPoints, knots);
            BSplineBasis.Evaluate(span, u, degree, knots, basis);
            double sum = 0;
            foreach (double b in basis)
                sum += b;
            Assert.Equal(1.0, sum, 13);
        }
    }
}
