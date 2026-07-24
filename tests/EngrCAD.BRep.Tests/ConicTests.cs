using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class ConicTests
{
    // A tilted orthonormal frame so nothing accidentally relies on axis alignment.
    private static readonly Vector3d ParabolaX = new(0, 0.6, 0.8);
    private static readonly Vector3d ParabolaY = new(1, 0, 0);
    private static readonly Vector3d Apex = new(1, 2, 3);
    private const double FocalLength = 1.5;

    private static Parabola3d MakeParabola() =>
        new(Apex, ParabolaX, ParabolaY, FocalLength, new Interval(-3, 4));

    private static readonly Vector3d HyperbolaU = new(0.6, 0.8, 0);
    private static readonly Vector3d HyperbolaV = new(-0.8, 0.6, 0);
    private static readonly Vector3d HyperbolaCenter = new(-1, 2, 0.5);
    private const double MajorRadius = 2.0;
    private const double MinorRadius = 0.8;

    private static Hyperbola3d MakeHyperbola() =>
        new(HyperbolaCenter, HyperbolaU * MajorRadius, HyperbolaV * MinorRadius, new Interval(-1.5, 2));

    [Fact]
    public void Parabola_SatisfiesFocalDefinition()
    {
        // Local coordinates must satisfy y² = 4fx, and every point must be equidistant
        // from the focus and the directrix (x = −f): dist(P, focus) = x + f.
        var parabola = MakeParabola();
        var focus = parabola.Focus;
        Assert.True(focus.AreEqual(Apex + ParabolaX * FocalLength, Tolerance.Default));

        for (int i = 0; i <= 100; i++)
        {
            double t = parabola.Domain.ParameterAt(i / 100.0);
            var local = parabola.PointAt(t) - Apex;
            double x = local.Dot(ParabolaX);
            double y = local.Dot(ParabolaY);
            Assert.Equal(4 * FocalLength * x, y * y, 12);
            Assert.Equal(y, t, 12); // parameter IS the local y coordinate
            Assert.Equal(x + FocalLength, parabola.PointAt(t).DistanceTo(focus), 12);
            Assert.True(Tolerance.Default.IsZero(local.Dot(parabola.Normal))); // planar
        }
    }

    [Fact]
    public void Parabola_TangentAtApexIsYDirection()
    {
        var parabola = MakeParabola();
        Assert.True(parabola.PointAt(0).AreEqual(Apex, Tolerance.Default));
        Assert.True(parabola.TangentAt(0).AreEqual(ParabolaY, Tolerance.Default));
        Assert.False(parabola.IsClosed);
    }

    [Fact]
    public void Parabola_DerivativesMatchDenseFiniteDifferences()
    {
        // Central differences of the (cubic-free) parabola are exact up to round-off:
        // truncation involves P‴ = 0, so agreement to 1e-8 shows the analytic override.
        var parabola = MakeParabola();
        double h = parabola.Domain.Length * 2e-6;
        double h2 = parabola.Domain.Length * 1e-4;
        for (int i = 1; i < 100; i++)
        {
            double t = parabola.Domain.ParameterAt(i / 100.0);
            var fd1 = (parabola.PointAt(t + h) - parabola.PointAt(t - h)) / (2 * h);
            Assert.True((parabola.DerivativeAt(t) - fd1).Length < 1e-8);
            var fd2 = (parabola.PointAt(t + h2) - parabola.PointAt(t) * 2 + parabola.PointAt(t - h2)) / (h2 * h2);
            Assert.True((parabola.SecondDerivativeAt(t) - fd2).Length < 1e-6);
        }
    }

    [Fact]
    public void Parabola_LengthMatchesDensePolyline()
    {
        // 20k chords underestimate the true length by ~κ²ℓ³/24 per chord (≈1e-8 total
        // here); the closed-form value must sit just above the chordal sum.
        var parabola = MakeParabola();
        double exact = parabola.Length();

        const int segments = 20_000;
        double chordal = 0;
        var previous = parabola.PointAt(parabola.Domain.Start);
        for (int i = 1; i <= segments; i++)
        {
            var point = parabola.PointAt(parabola.Domain.ParameterAt((double)i / segments));
            chordal += previous.DistanceTo(point);
            previous = point;
        }

        Assert.True(exact >= chordal - 1e-12, "chords must not exceed the exact length");
        Assert.True(exact - chordal < 1e-6 * exact, $"length mismatch: exact {exact:R} vs chordal {chordal:R}");
    }

    [Fact]
    public void Parabola_ValidatesInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Parabola3d(Apex, ParabolaX, ParabolaY, 0, new Interval(-1, 1)));
        Assert.Throws<ArgumentException>(() =>
            new Parabola3d(Apex, ParabolaX, ParabolaY, 1, new Interval(1, 1)));
        Assert.Throws<ArgumentException>(() =>
            new Parabola3d(Apex, ParabolaX, ParabolaY, 1, new Interval(0, double.PositiveInfinity)));
    }

    [Fact]
    public void Hyperbola_SatisfiesImplicitEquation()
    {
        var hyperbola = MakeHyperbola();
        for (int i = 0; i <= 100; i++)
        {
            double t = hyperbola.Domain.ParameterAt(i / 100.0);
            var local = hyperbola.PointAt(t) - HyperbolaCenter;
            double x = local.Dot(HyperbolaU) / MajorRadius;
            double y = local.Dot(HyperbolaV) / MinorRadius;
            Assert.Equal(1.0, x * x - y * y, 12);
            Assert.True(x >= 1 - 1e-12); // single branch, on the +X side
        }
        // Branch apex at t = 0.
        Assert.True(hyperbola.PointAt(0).AreEqual(
            HyperbolaCenter + HyperbolaU * MajorRadius, Tolerance.Default));
        Assert.False(hyperbola.IsClosed);
    }

    [Fact]
    public void Hyperbola_DerivativesMatchDenseFiniteDifferences()
    {
        var hyperbola = MakeHyperbola();
        double h = hyperbola.Domain.Length * 2e-6;
        for (int i = 1; i < 100; i++)
        {
            double t = hyperbola.Domain.ParameterAt(i / 100.0);
            var fd1 = (hyperbola.PointAt(t + h) - hyperbola.PointAt(t - h)) / (2 * h);
            Assert.True((hyperbola.DerivativeAt(t) - fd1).Length < 1e-8);
            var tangentError = hyperbola.TangentAt(t) - fd1.Normalized();
            Assert.True(tangentError.Length < 1e-9);
            // The defining ODE P″ = P − C holds exactly for the analytic override.
            var identity = hyperbola.SecondDerivativeAt(t) - (hyperbola.PointAt(t) - HyperbolaCenter);
            Assert.True(identity.Length < 1e-12);
        }
    }

    [Fact]
    public void Hyperbola_LengthMatchesDensePolyline()
    {
        var hyperbola = MakeHyperbola();
        double quadrature = hyperbola.Length();

        const int segments = 20_000;
        double chordal = 0;
        var previous = hyperbola.PointAt(hyperbola.Domain.Start);
        for (int i = 1; i <= segments; i++)
        {
            var point = hyperbola.PointAt(hyperbola.Domain.ParameterAt((double)i / segments));
            chordal += previous.DistanceTo(point);
            previous = point;
        }

        Assert.True(quadrature >= chordal - 1e-12, "chords must not exceed the arc length");
        Assert.True(quadrature - chordal < 1e-6 * quadrature,
            $"length mismatch: quadrature {quadrature:R} vs chordal {chordal:R}");
    }

    [Fact]
    public void Hyperbola_ValidatesInputs()
    {
        Assert.Throws<ArgumentException>(() =>
            new Hyperbola3d(HyperbolaCenter, HyperbolaU, HyperbolaV, new Interval(2, 2)));
        Assert.Throws<ArgumentException>(() =>
            new Hyperbola3d(HyperbolaCenter, HyperbolaU, HyperbolaV, new Interval(double.NegativeInfinity, 0)));
    }

    [Fact]
    public void Conics_ReversedAndTransformedDerivativesStayExact()
    {
        // The wrapper overrides must apply the chain rule, not fall back to finite
        // differences: reversal flips odd derivatives, keeps even ones.
        var parabola = MakeParabola();
        var reversed = parabola.Reversed();
        double t = 1.25;
        double mapped = parabola.Domain.Start + parabola.Domain.End - t;
        Assert.True((reversed.DerivativeAt(t) + parabola.DerivativeAt(mapped)).Length < 1e-13);
        Assert.True((reversed.SecondDerivativeAt(t) - parabola.SecondDerivativeAt(mapped)).Length < 1e-13);

        var transform = Matrix4d.CreateTranslation(new Vector3d(3, -1, 2));
        var moved = parabola.Transformed(transform);
        Assert.True((moved.DerivativeAt(t) - parabola.DerivativeAt(t)).Length < 1e-13);
        Assert.True((moved.SecondDerivativeAt(t) - parabola.SecondDerivativeAt(t)).Length < 1e-13);
    }
}
