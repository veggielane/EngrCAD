using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class CurveTests
{
    private const double Precision = 1e-12;

    [Fact]
    public void Line_EvaluatesLinearly()
    {
        var line = new Line3d((1, 2, 3), (5, 6, 7));
        Assert.Equal(new Vector3d(1, 2, 3), line.PointAt(0));
        Assert.Equal(new Vector3d(5, 6, 7), line.PointAt(1));
        Assert.Equal(new Vector3d(3, 4, 5), line.PointAt(0.5));
        Assert.True(line.TangentAt(0.3).AreEqual(new Vector3d(4, 4, 4).Normalized(), Tolerance.Default));
        Assert.False(line.IsClosed);
    }

    [Fact]
    public void Circle_EvaluatesOnCircle()
    {
        var circle = new Circle3d((1, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 2);
        Assert.True(circle.IsClosed);
        Assert.Equal(2 * Math.PI, circle.Domain.Length, Precision);

        Assert.True(circle.PointAt(0).AreEqual((3, 0, 0), Tolerance.Default));
        Assert.True(circle.PointAt(Math.PI / 2).AreEqual((1, 2, 0), Tolerance.Default));
        Assert.True(circle.TangentAt(0).AreEqual(Vector3d.UnitY, Tolerance.Default));
        Assert.Equal(Vector3d.UnitZ, circle.Axis);
    }

    [Fact]
    public void NurbsCircle_IsExact()
    {
        // Standard 9-point rational quadratic full circle, radius 2.
        double w = Math.Sqrt(2) / 2;
        double r = 2;
        var curve = new NurbsCurve(
            degree: 2,
            controlPoints:
            [
                (r, 0, 0), (r, r, 0), (0, r, 0), (-r, r, 0), (-r, 0, 0),
                (-r, -r, 0), (0, -r, 0), (r, -r, 0), (r, 0, 0),
            ],
            weights: [1, w, 1, w, 1, w, 1, w, 1],
            knots: [0, 0, 0, 0.25, 0.25, 0.5, 0.5, 0.75, 0.75, 1, 1, 1]);

        Assert.True(curve.IsClosed);
        for (int i = 0; i <= 100; i++)
        {
            double t = i / 100.0;
            Assert.Equal(r, curve.PointAt(t).Length, 9);
        }
        Assert.True(curve.PointAt(0).AreEqual((r, 0, 0), Tolerance.Default));
        Assert.True(curve.PointAt(0.25).AreEqual((0, r, 0), new Tolerance(1e-9, 1e-9)));
        Assert.True(curve.PointAt(0.5).AreEqual((-r, 0, 0), new Tolerance(1e-9, 1e-9)));
    }

    [Fact]
    public void NurbsCurve_UniformCubicInterpolatesEndpoints()
    {
        var curve = new NurbsCurve(
            degree: 3,
            controlPoints: [(0, 0, 0), (1, 2, 0), (2, -1, 0), (3, 0, 0)],
            weights: null,
            knots: [0, 0, 0, 0, 1, 1, 1, 1]); // Bézier form: clamped ends
        Assert.True(curve.PointAt(0).AreEqual((0, 0, 0), Tolerance.Default));
        Assert.True(curve.PointAt(1).AreEqual((3, 0, 0), Tolerance.Default));

        // Cubic Bézier midpoint: (P0 + 3P1 + 3P2 + P3) / 8.
        var expected = (new Vector3d(0, 0, 0) + 3 * new Vector3d(1, 2, 0) + 3 * new Vector3d(2, -1, 0) + new Vector3d(3, 0, 0)) / 8;
        Assert.True(curve.PointAt(0.5).AreEqual(expected, Tolerance.Default));
    }

    [Fact]
    public void NurbsCurve_ValidatesInputs()
    {
        Vector3d[] cps = [(0, 0, 0), (1, 0, 0), (2, 0, 0)];
        // Wrong knot count.
        Assert.Throws<ArgumentException>(() => new NurbsCurve(2, cps, null, [0, 0, 1, 1]));
        // Decreasing knots.
        Assert.Throws<ArgumentException>(() => new NurbsCurve(2, cps, null, [0, 0, 0, 1, 0.5, 1]));
        // Wrong weight count.
        Assert.Throws<ArgumentException>(() => new NurbsCurve(2, cps, [1, 1], [0, 0, 0, 1, 1, 1]));
    }
}
