using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class HelixTests
{
    // A tilted orthonormal frame so nothing accidentally relies on axis alignment.
    private static readonly Frame3d TiltedFrame =
        Frame3d.FromXY((1, -2, 0.5), (0, 0.6, 0.8), (1, 0, 0));

    private const double Radius = 1.5;
    private const double Pitch = 0.8;
    private const double Turns = 3.25;

    private static Helix3d MakeHelix() => new(TiltedFrame, Radius, Pitch, Turns);

    [Fact]
    public void Helix_LiesOnItsCylinderAndAdvancesByPitch()
    {
        var helix = MakeHelix();
        Assert.False(helix.IsClosed);
        Assert.Equal(2 * Math.PI * Turns, helix.Domain.End, 12);

        for (int i = 0; i <= 200; i++)
        {
            double t = helix.Domain.ParameterAt(i / 200.0);
            var local = TiltedFrame.ToLocal(helix.PointAt(t));
            // Constant distance from the axis.
            Assert.Equal(Radius, Math.Sqrt(local.X * local.X + local.Y * local.Y), 12);
            // The axial coordinate is exactly linear in the angle.
            Assert.Equal(Pitch * t / (2 * Math.PI), local.Z, 12);
            // The angular position IS the parameter (mod 2π).
            double angle = Math.Atan2(local.Y, local.X);
            double expected = Math.IEEERemainder(t, 2 * Math.PI);
            Assert.Equal(0, Math.IEEERemainder(angle - expected, 2 * Math.PI), 10);
        }

        // Start point sits on the frame's X axis; one full turn advances by one pitch.
        Assert.True(helix.PointAt(0).AreEqual(TiltedFrame.Origin + TiltedFrame.X * Radius, Tolerance.Default));
        var perTurn = helix.PointAt(2 * Math.PI) - helix.PointAt(0);
        Assert.True(perTurn.AreEqual(TiltedFrame.Z * Pitch, Tolerance.Default));
    }

    [Fact]
    public void Helix_DerivativesMatchDenseFiniteDifferences()
    {
        var helix = MakeHelix();
        double h = 2e-6;
        for (int i = 1; i < 100; i++)
        {
            double t = helix.Domain.ParameterAt(i / 100.0);
            var fd1 = (helix.PointAt(t + h) - helix.PointAt(t - h)) / (2 * h);
            Assert.True((helix.DerivativeAt(t) - fd1).Length < 1e-8);
            Assert.True((helix.TangentAt(t) - fd1.Normalized()).Length < 1e-9);

            double h2 = 1e-4;
            var fd2 = (helix.PointAt(t + h2) - helix.PointAt(t) * 2 + helix.PointAt(t - h2)) / (h2 * h2);
            Assert.True((helix.SecondDerivativeAt(t) - fd2).Length < 1e-6);

            // Constant speed √(r² + (p/2π)²) — the closed-form arc length depends on it.
            Assert.Equal(
                Math.Sqrt(Radius * Radius + Math.Pow(Pitch / (2 * Math.PI), 2)),
                helix.DerivativeAt(t).Length, 12);

            // The second derivative points exactly at the axis (no axial component).
            Assert.True(Tolerance.Default.IsZero(helix.SecondDerivativeAt(t).Dot(TiltedFrame.Z)));
        }
    }

    [Fact]
    public void Helix_LengthMatchesClosedFormAndChords()
    {
        var helix = MakeHelix();
        double exact = helix.Length();
        Assert.Equal(Turns * Math.Sqrt(Math.Pow(2 * Math.PI * Radius, 2) + Pitch * Pitch), exact, 12);

        const int segments = 20_000;
        double chordal = 0;
        var previous = helix.PointAt(0);
        for (int i = 1; i <= segments; i++)
        {
            var point = helix.PointAt(helix.Domain.ParameterAt((double)i / segments));
            chordal += previous.DistanceTo(point);
            previous = point;
        }
        Assert.True(exact >= chordal - 1e-12, "chords must not exceed the exact length");
        Assert.True(exact - chordal < 1e-6 * exact, $"length mismatch: exact {exact:R} vs chordal {chordal:R}");
    }

    [Fact]
    public void Helix_NegativePitchDescendsAndMirrors()
    {
        var down = new Helix3d(TiltedFrame, Radius, -Pitch, 1);
        var advance = (down.PointAt(2 * Math.PI) - down.PointAt(0)).Dot(TiltedFrame.Z);
        Assert.Equal(-Pitch, advance, 12);
        Assert.Equal(Math.Atan2(Pitch, 2 * Math.PI * Radius), down.LeadAngle, 12);
    }

    [Fact]
    public void Helix_AxisConstructorUsesTheSharedFrameConvention()
    {
        // The origin+axis constructor must delegate to Frame3d.FromNormal — the single
        // arbitrary-perpendicular convention — so co-axial geometry welds bit-for-bit.
        var origin = new Vector3d(2, 1, -3);
        var axis = new Vector3d(0.3, -0.4, 0.5);
        var helix = new Helix3d(origin, axis, Radius, Pitch, 2);
        var frame = Frame3d.FromNormal(origin, axis);
        Assert.True(helix.PointAt(0).AreEqual(origin + frame.X * Radius, Tolerance.Default));
        Assert.Equal(0, helix.Frame.X.DistanceTo(frame.X), 15);
    }

    [Fact]
    public void Helix_ReversedAndTransformedDerivativesStayExact()
    {
        var helix = MakeHelix();
        var reversed = helix.Reversed();
        double t = 1.75;
        double mapped = helix.Domain.Start + helix.Domain.End - t;
        Assert.True((reversed.DerivativeAt(t) + helix.DerivativeAt(mapped)).Length < 1e-12);
        Assert.True((reversed.SecondDerivativeAt(t) - helix.SecondDerivativeAt(mapped)).Length < 1e-12);

        var transform = Matrix4d.CreateRotationZ(0.7) * Matrix4d.CreateTranslation((3, -1, 2));
        var moved = helix.Transformed(transform);
        var expected = transform.TransformVector(helix.DerivativeAt(t));
        Assert.True((moved.DerivativeAt(t) - expected).Length < 1e-12);
    }

    [Fact]
    public void Helix_ValidatesInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Helix3d(TiltedFrame, 0, Pitch, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Helix3d(TiltedFrame, -1, Pitch, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Helix3d(TiltedFrame, Radius, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Helix3d(TiltedFrame, Radius, double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Helix3d(TiltedFrame, Radius, Pitch, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Helix3d(TiltedFrame, Radius, Pitch, -2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Helix3d(TiltedFrame, Radius, Pitch, double.PositiveInfinity));
    }
}
