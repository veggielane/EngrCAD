using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="Curve3d.ArcLength(double, double, double)"/>, its inverse
/// <see cref="Curve3d.ParameterAtLength"/>, and <see cref="ArcLengthTable3d"/> — checked
/// against closed forms where they exist and against dense numerical integration where they
/// do not.
/// </summary>
public class ArcLength3dTests
{
    /// <summary>Trapezoid rule over |C′| at 200 001 samples — slow, dumb, and independent of
    /// everything the adaptive quadrature does.</summary>
    private static double BruteForceLength(Curve3d curve, int samples = 200_000)
    {
        var domain = curve.Domain;
        double total = 0;
        var previous = curve.PointAt(domain.Start);
        for (int i = 1; i <= samples; i++)
        {
            var next = curve.PointAt(domain.ParameterAt((double)i / samples));
            total += (next - previous).Length;
            previous = next;
        }
        return total;
    }

    [Fact]
    public void Line_IsExact()
    {
        var line = new Line3d(new(1, 2, 3), new(4, 6, 3));
        Assert.Equal(5.0, line.ArcLength(), 15);
        Assert.Equal(2.5, line.ArcLength(0, 0.5), 15);
        // A reversed direction returns a negative length, as the contract says.
        Assert.Equal(-2.5, line.ArcLength(0.5, 0));
    }

    [Fact]
    public void Circle_IsExact()
    {
        var circle = new Circle3d(new(0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 4);
        Assert.Equal(8 * Math.PI, circle.ArcLength(), 12);
        Assert.Equal(2 * Math.PI, circle.ArcLength(0, Math.PI / 2), 12);
    }

    [Fact]
    public void Helix_MatchesTheClosedForm()
    {
        // One turn of radius 3 with pitch 5: L = sqrt((2*pi*r)^2 + p^2).
        var helix = new Helix3d(Vector3d.Zero, Vector3d.UnitZ, radius: 3, pitch: 5, turns: 1);
        double expected = Math.Sqrt(Math.Pow(2 * Math.PI * 3, 2) + 25);
        Assert.Equal(expected, helix.ArcLength(), 10);
        Assert.Equal(helix.Length(), helix.ArcLength(), 15);
        // Constant speed: half the turning angle is exactly half the length.
        Assert.Equal(expected / 2, helix.ArcLength(0, Math.PI), 10);
    }

    [Fact]
    public void Parabola_MatchesTheClosedFormAndDenseIntegration()
    {
        var parabola = new Parabola3d(
            Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, focalLength: 1.5, new Interval(-4, 4));
        double brute = BruteForceLength(parabola);
        Assert.Equal(brute, parabola.ArcLength(), 5);
        Assert.Equal(parabola.Length(), parabola.ArcLength(), 15);
    }

    [Fact]
    public void Ellipse_MatchesDenseIntegration()
    {
        // An ellipse's arc length is an elliptic integral with no elementary closed form,
        // so this is the quadrature path exercised against brute force.
        var ellipse = new Ellipse3d(Vector3d.Zero, Vector3d.UnitX * 5, Vector3d.UnitY * 3);
        Assert.Equal(BruteForceLength(ellipse), ellipse.ArcLength(), 6);
    }

    [Fact]
    public void Nurbs_MatchesDenseIntegration()
    {
        // A cubic B-spline through six points: the adaptive quadrature rides the curve's
        // EXACT analytic derivative, so it should beat 200 000 chords comfortably.
        var spline = NurbsCurve.InterpolatePoints(
        [
            new(0, 0, 0), new(2, 3, 1), new(5, 1, -2), new(8, 4, 0), new(11, 0, 3), new(14, 2, 1),
        ]);
        double brute = BruteForceLength(spline);
        Assert.Equal(brute, spline.ArcLength(), 6);
    }

    [Fact]
    public void RationalArc_MatchesTheExactCircularLength()
    {
        // NurbsCurve.Arc is an EXACT rational circle, so its length must equal r*sweep even
        // though the arc-length integrand is far from constant in the NURBS parameter.
        var arc = NurbsCurve.Arc(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 4, 0, 2.1);
        Assert.Equal(4 * 2.1, arc.ArcLength(), 8);
    }

    [Fact]
    public void Polyline_IsExactlyItsOwnParameter()
    {
        // A polyline IS chord-length parameterized, so length and parameter are the same
        // number — and quadrature over a piecewise-constant speed would be worse, not better.
        var polyline = new PolylineCurve3d([new(0, 0, 0), new(3, 4, 0), new(3, 4, 12)]);
        Assert.Equal(17.0, polyline.ArcLength(), 15);
        Assert.Equal(5.0, polyline.ArcLength(0, 5));
        Assert.Equal(polyline.Domain.End, polyline.ArcLength());
    }

    [Fact]
    public void ReversedCurve_KeepsTheBaseCurvesExactLength()
    {
        var circle = new Circle3d(new(0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 4);
        var reversed = circle.Reversed();
        Assert.Equal(8 * Math.PI, reversed.ArcLength(), 12);
        Assert.Equal(2 * Math.PI, reversed.ArcLength(0, Math.PI / 2), 12);
    }

    [Fact]
    public void CurveSegment_InheritsItsBasesExactLength()
    {
        var circle = new Circle3d(new(0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 4);
        var quarter = new CurveSegment(circle, 0, Math.PI / 2);
        Assert.Equal(2 * Math.PI, quarter.ArcLength(), 12);
        Assert.Equal(Math.PI, quarter.ArcLength(0, 0.5), 12);
    }

    // ---- the inverse ----

    [Fact]
    public void ParameterAtLength_InvertsArcLength_OnAConstantSpeedCurve()
    {
        var circle = new Circle3d(new(0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 4);
        for (int i = 0; i <= 10; i++)
        {
            double s = 8 * Math.PI * i / 10;
            Assert.Equal(s / 4, circle.ParameterAtLength(s), 9);
        }
    }

    [Fact]
    public void ParameterAtLength_InvertsArcLength_OnAVaryingSpeedCurve()
    {
        var spline = NurbsCurve.InterpolatePoints(
            [new(0, 0, 0), new(2, 3, 1), new(5, 1, -2), new(8, 4, 0), new(11, 0, 3)]);
        double total = spline.ArcLength();
        for (int i = 1; i < 10; i++)
        {
            double s = total * i / 10;
            double t = spline.ParameterAtLength(s);
            Assert.Equal(s, spline.ArcLength(spline.Domain.Start, t), 9);
        }
    }

    [Fact]
    public void ParameterAtLength_ClampsOutsideTheDomain()
    {
        var line = new Line3d(Vector3d.Zero, new(10, 0, 0));
        Assert.Equal(line.Domain.Start, line.ParameterAtLength(-5));
        Assert.Equal(line.Domain.End, line.ParameterAtLength(1e6));
    }

    // ---- the table ----

    [Fact]
    public void Table_AgreesWithTheDirectInverse()
    {
        var spline = NurbsCurve.InterpolatePoints(
            [new(0, 0, 0), new(2, 3, 1), new(5, 1, -2), new(8, 4, 0), new(11, 0, 3)]);
        var table = new ArcLengthTable3d(spline);
        Assert.Equal(spline.ArcLength(), table.TotalLength, 9);
        for (int i = 0; i <= 20; i++)
        {
            double s = table.TotalLength * i / 20;
            Assert.Equal(spline.ParameterAtLength(s), table.ParameterAtLength(s), 8);
        }
    }

    [Fact]
    public void Table_ResamplesAtEqualArcLength()
    {
        // The point of the table: consecutive samples are equally spaced ALONG the curve,
        // which uniform parameters on a varying-speed curve are not.
        var spline = NurbsCurve.InterpolatePoints(
            [new(0, 0, 0), new(2, 3, 1), new(5, 1, -2), new(8, 4, 0), new(11, 0, 3)]);
        var table = new ArcLengthTable3d(spline);
        var points = table.SampleByLength(40);

        double step = table.TotalLength / 40;
        for (int i = 1; i < points.Length; i++)
        {
            double t0 = table.ParameterAtLength(step * (i - 1));
            double t1 = table.ParameterAtLength(step * i);
            Assert.Equal(step, spline.ArcLength(t0, t1), 6);
        }
        Assert.Equal(spline.PointAt(spline.Domain.Start), points[0]);
    }
}
