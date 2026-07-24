using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class OffsetCurveTests
{
    // Planar (z = 0) waypoints for a smooth interpolated base curve.
    private static readonly Vector3d[] PlanarPoints =
        [(0, 0, 0), (1, 2, 0), (2.5, 2.2, 0), (4, 1, 0), (5, -0.5, 0), (6.5, 0, 0)];

    [Fact]
    public void OffsetOfCircle_IsConcentricCircleExactly()
    {
        // Tilted circle so the frame algebra is exercised in 3D. With normal = axis and
        // CCW traversal, n̂ × T̂ points INWARD, so positive d shrinks the radius.
        var center = new Vector3d(1, 2, 3);
        var x = new Vector3d(1, 0, 0);
        var y = new Vector3d(0, 0.6, 0.8);
        var circle = new Circle3d(center, x, y, radius: 2.0);

        foreach (double distance in new[] { 0.5, -0.75 })
        {
            var offset = new OffsetCurve3d(circle, circle.Axis, distance);
            var expected = new Circle3d(center, x, y, 2.0 - distance);
            Assert.True(offset.IsClosed);
            Assert.Equal(circle.Domain, offset.Domain);
            for (int i = 0; i <= 128; i++)
            {
                double t = offset.Domain.ParameterAt(i / 128.0);
                Assert.True(offset.PointAt(t).AreEqual(expected.PointAt(t), new Tolerance(1e-13, 1e-13)));
                Assert.True((offset.DerivativeAt(t) - expected.DerivativeAt(t)).Length < 1e-13);
                Assert.True((offset.TangentAt(t) - expected.TangentAt(t)).Length < 1e-13);
            }
        }
    }

    [Fact]
    public void OffsetOfLine_IsTranslatedLine()
    {
        // κ = 0, so the offset is a rigid translation by d·(n̂ × T̂) and the derivative
        // is exactly the base chord.
        var line = new Line3d((0, 0, 0), (4, 3, 0));
        var offset = new OffsetCurve3d(line, Vector3d.UnitZ, 1.0);
        var shift = new Vector3d(-0.6, 0.8, 0); // Z × (0.8, 0.6, 0)
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0;
            Assert.True(offset.PointAt(t).AreEqual(line.PointAt(t) + shift, new Tolerance(1e-14, 1e-14)));
        }
        Assert.True((offset.DerivativeAt(0.5) - new Vector3d(4, 3, 0)).Length < 1e-14);
    }

    [Fact]
    public void Offset_DerivativeMatchesDenseFiniteDifferences()
    {
        // The analytic O′ = (1 − dκ)·C′ must agree with central differences of the
        // offset's own PointAt (which uses only the base's exact derivatives).
        var baseCurve = NurbsCurve.InterpolatePoints(PlanarPoints);
        var offset = new OffsetCurve3d(baseCurve, Vector3d.UnitZ, 0.15);
        double h = offset.Domain.Length * 2e-6;
        for (int i = 1; i < 100; i++)
        {
            double t = offset.Domain.ParameterAt(i / 100.0);
            // O″ jumps at interior knots (κ′ is discontinuous there); keep the FD
            // window clear of them so the central-difference error model holds.
            if (baseCurve.Knots.Any(k => Math.Abs(k - t) < 1e-4))
                continue;
            var fd = (offset.PointAt(t + h) - offset.PointAt(t - h)) / (2 * h);
            double error = (offset.DerivativeAt(t) - fd).Length;
            Assert.True(error < 1e-7, $"offset derivative vs FD error {error:E3} at t={t}");
        }
    }

    [Fact]
    public void Offset_StaysAtExactDistanceInPlane()
    {
        // |n̂ × T̂| = 1 for a planar base, so every offset point sits exactly |d| from
        // its base point, and tangents stay in the plane.
        var baseCurve = NurbsCurve.InterpolatePoints(PlanarPoints);
        var offset = new OffsetCurve3d(baseCurve, Vector3d.UnitZ, -0.4);
        for (int i = 0; i <= 100; i++)
        {
            double t = offset.Domain.ParameterAt(i / 100.0);
            Assert.Equal(0.4, offset.PointAt(t).DistanceTo(baseCurve.PointAt(t)), 12);
            Assert.True(Math.Abs(offset.TangentAt(t).Dot(Vector3d.UnitZ)) < 1e-9);
        }
    }

    [Fact]
    public void Offset_RejectsNonPlanarBase()
    {
        var nonPlanar = NurbsCurve.InterpolatePoints(
            [(0, 0, 0), (1, 2, 0.5), (2.5, 2.2, 1.5), (4, 1, 2)]);
        Assert.Throws<ArgumentException>(() => new OffsetCurve3d(nonPlanar, Vector3d.UnitZ, 0.2));
    }

    [Fact]
    public void Offset_UnderlyingForwardsToBaseForSamplingRules()
    {
        // Sampling-rule forwarding only — the offset's POSITION is never on Underlying
        // (kernel-wide rule: sample the actual curve).
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 3.0);
        var offset = new OffsetCurve3d(circle, Vector3d.UnitZ, 0.25);
        Assert.Same(circle, offset.Underlying);
        Assert.Same(circle, offset.Base);
        Assert.Equal(0.25, offset.Distance);
        Assert.True(offset.PlaneNormal.AreEqual(Vector3d.UnitZ, Tolerance.Default));
    }

    [Fact]
    public void Offset_OfNewConicsUsesExactCurvature()
    {
        // Parabola offset: at the apex T̂ = Ŷ and C″ = X̂/(2f), so the signed curvature
        // about n̂ = Ẑ is κ(0) = C″·(Ẑ × Ŷ)/|C′|³ = −1/(2f) (curvature center on the +X
        // side, offset direction on the −X side) and O′(0) = (1 + d/(2f))·C′(0).
        var parabola = new Parabola3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 1.5, new Interval(-2, 2));
        double d = 0.6;
        var offset = new OffsetCurve3d(parabola, parabola.Normal, d);
        double expectedScale = 1 + d / (2 * 1.5);
        Assert.True((offset.DerivativeAt(0) - parabola.DerivativeAt(0) * expectedScale).Length < 1e-13);
        // Apex offsets along n̂ × Ŷ = −X̂ … for normal Z, tangent Y at apex: Z × Y = −X.
        Assert.True(offset.PointAt(0).AreEqual(new Vector3d(-d, 0, 0), new Tolerance(1e-13, 1e-13)));
    }
}
