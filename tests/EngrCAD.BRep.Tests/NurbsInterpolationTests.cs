using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class NurbsInterpolationTests
{
    private static readonly Vector3d[] SamplePoints =
        [(0, 0, 0), (1, 2, 0.5), (2.5, 2.2, 1.5), (4, 1, 2), (5, -0.5, 2.2), (6.5, 0, 3)];

    /// <summary>The chord-length parameters the interpolation contract documents (normalized to [0, 1]).</summary>
    private static double[] ChordParameters(IReadOnlyList<Vector3d> points, bool closed)
    {
        int n = points.Count;
        var t = new double[closed ? n + 1 : n];
        double total = 0;
        for (int i = 1; i < n; i++)
        {
            total += points[i].DistanceTo(points[i - 1]);
            t[i] = total;
        }
        if (closed)
        {
            total += points[0].DistanceTo(points[n - 1]);
            t[n] = total;
        }
        for (int i = 1; i < t.Length; i++)
            t[i] /= total;
        t[^1] = 1.0;
        return t;
    }

    [Fact]
    public void InterpolatePoints_PassesThroughEveryPoint()
    {
        var curve = NurbsCurve.InterpolatePoints(SamplePoints);
        var parameters = ChordParameters(SamplePoints, closed: false);

        Assert.Equal(3, curve.Degree);
        Assert.Equal(Interval.Unit, curve.Domain);
        for (int i = 0; i < SamplePoints.Length; i++)
        {
            double error = curve.PointAt(parameters[i]).DistanceTo(SamplePoints[i]);
            Assert.True(error < 1e-9, $"point {i} missed by {error:E3}");
        }
    }

    [Fact]
    public void InterpolatePoints_NaturalEndConditions()
    {
        // The tridiagonal solve enforces C″ = 0 at both ends; residual is solver round-off.
        var curve = NurbsCurve.InterpolatePoints(SamplePoints);
        Assert.True(curve.SecondDerivativeAt(0).Length < 1e-9);
        Assert.True(curve.SecondDerivativeAt(1).Length < 1e-9);
    }

    [Fact]
    public void InterpolatePoints_IsC2AtInteriorKnots()
    {
        // Simple interior knots make a cubic C2. Evaluating the exact derivatives at
        // knot ± h (h = 1e-9) bounds any jump by 2h·|next-derivative|: with |C″| ~ 1e2
        // and |C‴| ~ 1e3 here, a continuous curve shows ≲ 1e-6 while a discontinuity
        // would show O(1) — the assertions sit two orders above the continuous case.
        var curve = NurbsCurve.InterpolatePoints(SamplePoints);
        var parameters = ChordParameters(SamplePoints, closed: false);
        const double h = 1e-9;
        for (int j = 1; j <= SamplePoints.Length - 2; j++)
        {
            double firstJump = (curve.DerivativeAt(parameters[j] + h) - curve.DerivativeAt(parameters[j] - h)).Length;
            double secondJump = (curve.SecondDerivativeAt(parameters[j] + h) - curve.SecondDerivativeAt(parameters[j] - h)).Length;
            Assert.True(firstJump < 1e-5, $"C′ jump {firstJump:E3} at knot {j}");
            Assert.True(secondJump < 1e-4, $"C″ jump {secondJump:E3} at knot {j}");
        }
    }

    [Fact]
    public void InterpolatePoints_TwoPoints_IsTheChord()
    {
        var curve = NurbsCurve.InterpolatePoints([(1, 1, 1), (3, 5, 2)]);
        Assert.Equal(1, curve.Degree);
        Assert.True(curve.PointAt(0).AreEqual((1, 1, 1), Tolerance.Default));
        Assert.True(curve.PointAt(0.5).AreEqual((2, 3, 1.5), Tolerance.Default));
        Assert.True(curve.PointAt(1).AreEqual((3, 5, 2), Tolerance.Default));
    }

    [Fact]
    public void InterpolatePoints_ValidatesInputs()
    {
        Assert.Throws<ArgumentException>(() => NurbsCurve.InterpolatePoints([(0, 0, 0)]));
        // Duplicate consecutive points.
        Assert.Throws<ArgumentException>(() =>
            NurbsCurve.InterpolatePoints([(0, 0, 0), (1, 0, 0), (1, 0, 0), (2, 1, 0)]));
        // Closed needs at least 3 points.
        Assert.Throws<ArgumentException>(() =>
            NurbsCurve.InterpolatePoints([(0, 0, 0), (1, 0, 0)], closed: true));
        // Closed must not repeat the seam point.
        Assert.Throws<ArgumentException>(() =>
            NurbsCurve.InterpolatePoints([(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 0)], closed: true));
    }

    [Fact]
    public void InterpolatePoints_ArcSamples_StayNearTheArc()
    {
        // 17 samples of a unit quarter circle. Between samples the cubic interpolant is
        // O(h⁴)-accurate in the interior, but the natural end conditions force C″ = 0
        // where the true curvature is 1, an O(h²) end effect (measured: 4.7e-4 max at
        // the ends, 8.4e-6 in the interior; bounds sit ~2× above).
        const int samples = 17;
        var points = new Vector3d[samples];
        for (int i = 0; i < samples; i++)
        {
            double angle = Math.PI / 2 * i / (samples - 1);
            points[i] = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);
        }
        var fit = NurbsCurve.InterpolatePoints(points);

        double maxDeviation = 0, maxInteriorDeviation = 0;
        for (int i = 0; i <= 2000; i++)
        {
            double t = i / 2000.0;
            double deviation = Math.Abs(fit.PointAt(t).Length - 1.0);
            maxDeviation = Math.Max(maxDeviation, deviation);
            if (t is >= 0.2 and <= 0.8)
                maxInteriorDeviation = Math.Max(maxInteriorDeviation, deviation);
        }
        Assert.True(maxDeviation < 1e-3, $"max radial deviation {maxDeviation:E3}");
        Assert.True(maxInteriorDeviation < 2e-5, $"interior radial deviation {maxInteriorDeviation:E3}");
    }

    [Fact]
    public void InterpolatePoints_Closed_PassesThroughPointsAndSeamIsC2()
    {
        const int count = 8;
        const double radius = 2.0;
        var points = new Vector3d[count];
        for (int i = 0; i < count; i++)
        {
            double angle = 2 * Math.PI * i / count;
            points[i] = new Vector3d(radius * Math.Cos(angle), radius * Math.Sin(angle), 0);
        }
        var curve = NurbsCurve.InterpolatePoints(points, closed: true);
        Assert.True(curve.IsClosed);

        var parameters = ChordParameters(points, closed: true);
        for (int i = 0; i < count; i++)
        {
            double error = curve.PointAt(parameters[i]).DistanceTo(points[i]);
            Assert.True(error < 1e-9, $"point {i} missed by {error:E3}");
        }

        // The periodic construction wraps the control points, so position, tangent, and
        // curvature at the seam agree to machine precision — no stitching involved.
        Assert.True(curve.PointAt(0).DistanceTo(curve.PointAt(1)) < 1e-12);
        Assert.True((curve.TangentAt(0) - curve.TangentAt(1)).Length < 1e-12);
        Assert.True((curve.SecondDerivativeAt(0) - curve.SecondDerivativeAt(1)).Length < 1e-9);

        // 8 points around a circle keep the interpolant within ~2.4e-3 of it (O(h⁴)).
        for (int i = 0; i <= 2000; i++)
            Assert.True(Math.Abs(curve.PointAt(i / 2000.0).Length - radius) < 5e-3);
    }

    [Fact]
    public void NurbsTangent_MatchesAnalyticTangentOnArc()
    {
        // Rational arc: the exact derivative must handle the quotient rule. Ground truth
        // is the analytic circle tangent axis × (P − center) — exact at every parameter.
        var center = new Vector3d(1, 2, 3);
        var x = new Vector3d(1, 0, 0);
        var y = new Vector3d(0, 0.6, 0.8);
        var axis = x.Cross(y);
        var arc = NurbsCurve.Arc(center, x, y, radius: 2.0, startAngle: -0.3, endAngle: 2.2);

        for (int i = 0; i <= 200; i++)
        {
            double t = arc.Domain.ParameterAt(i / 200.0);
            var analytic = axis.Cross(arc.PointAt(t) - center).Normalized();
            double error = (arc.TangentAt(t) - analytic).Length;
            Assert.True(error < 1e-12, $"tangent error {error:E3} at t={t}");
        }
    }

    [Fact]
    public void NurbsTangent_MatchesDenseFiniteDifferences()
    {
        // h = 2e-6 keeps the central-difference truncation (~h²·|C‴|/|C′|) and round-off
        // (~ε/h) both below 1e-10, so agreement to 1e-9 shows the analytic derivative is
        // exact — the reverse of the old default, where the FD tangent WAS the answer.
        var interpolated = NurbsCurve.InterpolatePoints(SamplePoints);
        var arc = NurbsCurve.Arc(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1.5, 0.2, 2.9);
        foreach (var curve in new[] { interpolated, arc })
        {
            double h = curve.Domain.Length * 2e-6;
            for (int i = 1; i < 100; i++)
            {
                double t = curve.Domain.ParameterAt(i / 100.0);
                var finiteDifference = (curve.PointAt(t + h) - curve.PointAt(t - h)).Normalized();
                double error = (curve.TangentAt(t) - finiteDifference).Length;
                Assert.True(error < 1e-9, $"tangent vs FD error {error:E3} at t={t}");
            }
        }
    }

    [Fact]
    public void InterpolatedPath_SweepsToValidSolid()
    {
        // Exercises rotation-minimizing frames against the exact tangents; the closed-mesh
        // tessellation check lives in EngrCAD.Interop.Tests (ModelingTessellationTests).
        var path = NurbsCurve.InterpolatePoints(
            [(0, 0, 0), (0, 0.3, 1), (0, 0.9, 2), (0.4, 1.6, 2.8), (1.0, 2.2, 3.4)]);
        var tangent = path.TangentAt(path.Domain.Start);
        var x = tangent.Cross(Vector3d.UnitX).Normalized();
        var y = tangent.Cross(x);
        var profile = Profile.Circle((0, 0, 0), x, y, 0.25);

        var solid = SolidFactory.Sweep(profile, path);
        solid.Validate();
        Assert.True(solid.SatisfiesEulerFormula(genus: 0));
    }
}
