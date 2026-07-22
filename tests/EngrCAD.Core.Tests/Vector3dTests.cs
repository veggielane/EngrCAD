using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Vector3dTests
{
    private const double Precision = 1e-12;

    [Fact]
    public void Arithmetic_Componentwise()
    {
        var a = new Vector3d(1, 2, 3);
        var b = new Vector3d(4, 5, 6);
        Assert.Equal(new Vector3d(5, 7, 9), a + b);
        Assert.Equal(new Vector3d(-3, -3, -3), a - b);
        Assert.Equal(new Vector3d(2, 4, 6), a * 2);
        Assert.Equal(new Vector3d(0.5, 1, 1.5), a / 2);
        Assert.Equal(new Vector3d(-1, -2, -3), -a);
    }

    [Fact]
    public void Dot_And_Length()
    {
        var a = new Vector3d(1, 2, 3);
        var b = new Vector3d(4, 5, 6);
        Assert.Equal(32, a.Dot(b), Precision);
        Assert.Equal(Math.Sqrt(14), a.Length, Precision);
        Assert.Equal(14, a.LengthSquared, Precision);
    }

    [Fact]
    public void Cross_IsOrthogonalAndRightHanded()
    {
        Assert.Equal(Vector3d.UnitZ, Vector3d.UnitX.Cross(Vector3d.UnitY));

        var a = new Vector3d(1.5, -2.3, 0.7);
        var b = new Vector3d(-0.4, 3.1, 2.2);
        var c = a.Cross(b);
        Assert.Equal(0, c.Dot(a), Precision);
        Assert.Equal(0, c.Dot(b), Precision);
    }

    [Fact]
    public void Normalized_ProducesUnitVector()
    {
        var v = new Vector3d(3, 4, 12).Normalized();
        Assert.Equal(1.0, v.Length, Precision);
    }

    [Fact]
    public void TryNormalize_FailsForZeroVector()
    {
        Assert.False(Vector3d.Zero.TryNormalize(Tolerance.Default, out _));
        Assert.Throws<InvalidOperationException>(() => Vector3d.Zero.Normalized());
    }

    [Fact]
    public void AngleTo_KnownAngles()
    {
        Assert.Equal(Math.PI / 2, Vector3d.UnitX.AngleTo(Vector3d.UnitY), Precision);
        Assert.Equal(0, Vector3d.UnitX.AngleTo(new Vector3d(5, 0, 0)), Precision);
        Assert.Equal(Math.PI, Vector3d.UnitX.AngleTo(new Vector3d(-2, 0, 0)), Precision);
        Assert.Equal(Math.PI / 4, Vector3d.UnitX.AngleTo(new Vector3d(1, 1, 0)), Precision);
    }

    [Fact]
    public void ParallelAndPerpendicular_UseAngularTolerance()
    {
        var tol = Tolerance.Default;
        Assert.True(Vector3d.UnitX.IsParallelTo(new Vector3d(-3, 0, 0), tol));
        Assert.True(Vector3d.UnitX.IsPerpendicularTo(new Vector3d(0, 2, 2), tol));
        Assert.False(Vector3d.UnitX.IsParallelTo(new Vector3d(1, 0.1, 0), tol));
    }

    [Fact]
    public void ArbitraryPerpendicular_IsUnitAndOrthogonal()
    {
        Vector3d[] samples =
        [
            Vector3d.UnitX, Vector3d.UnitY, Vector3d.UnitZ,
            new(1, 1, 1), new(-0.3, 12.0, 4.5), new(1e-3, -2e6, 3),
        ];
        foreach (var v in samples)
        {
            var p = v.ArbitraryPerpendicular(Tolerance.Default);
            Assert.Equal(1.0, p.Length, Precision);
            Assert.Equal(0, p.Dot(v.Normalized()), 1e-9);
        }
    }

    [Fact]
    public void MinMaxLerp()
    {
        var a = new Vector3d(1, 5, -2);
        var b = new Vector3d(3, 2, 4);
        Assert.Equal(new Vector3d(1, 2, -2), Vector3d.Min(a, b));
        Assert.Equal(new Vector3d(3, 5, 4), Vector3d.Max(a, b));
        Assert.Equal(new Vector3d(2, 3.5, 1), Vector3d.Lerp(a, b, 0.5));
    }

    [Fact]
    public void ImplicitConversion_FromTuple()
    {
        Vector3d v = (1.0, 2.0, 3.0);
        Assert.Equal(new Vector3d(1, 2, 3), v);

        // Flows through parameters expecting Vector3d, e.g. Aabb construction.
        var box = new Aabb((0, 0, 0), (1, 1, 1));
        Assert.Equal(Vector3d.One, box.Max);

        Vector2d uv = (0.25, 0.75);
        Assert.Equal(new Vector2d(0.25, 0.75), uv);
    }

    [Fact]
    public void AreEqual_UsesDistance()
    {
        var tol = new Tolerance(1e-6, 1e-6);
        var a = new Vector3d(1, 2, 3);
        Assert.True(a.AreEqual(new Vector3d(1 + 1e-7, 2, 3), tol));
        Assert.False(a.AreEqual(new Vector3d(1 + 1e-5, 2, 3), tol));
    }
}
