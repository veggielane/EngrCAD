using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class Matrix4dTests
{
    private const double Precision = 1e-9;

    private static void AssertEqual(in Vector3d expected, in Vector3d actual, double precision = Precision)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
        Assert.Equal(expected.Z, actual.Z, precision);
    }

    [Fact]
    public void Identity_LeavesPointsUnchanged()
    {
        var p = new Vector3d(1.5, -2, 7);
        AssertEqual(p, Matrix4d.Identity.TransformPoint(p));
        AssertEqual(p, Matrix4d.Identity.TransformVector(p));
    }

    [Fact]
    public void Translation_MovesPointsButNotVectors()
    {
        var m = Matrix4d.CreateTranslation(new Vector3d(10, 20, 30));
        AssertEqual(new Vector3d(11, 22, 33), m.TransformPoint(new Vector3d(1, 2, 3)));
        AssertEqual(new Vector3d(1, 2, 3), m.TransformVector(new Vector3d(1, 2, 3)));
    }

    [Fact]
    public void RotationZ_QuarterTurn()
    {
        var m = Matrix4d.CreateRotationZ(Math.PI / 2);
        AssertEqual(new Vector3d(0, 1, 0), m.TransformPoint(new Vector3d(1, 0, 0)));
    }

    [Fact]
    public void AxisAngle_MatchesAxisRotations()
    {
        double angle = 0.7361;
        var fromAxis = Matrix4d.CreateFromAxisAngle(Vector3d.UnitY, angle);
        var direct = Matrix4d.CreateRotationY(angle);
        var p = new Vector3d(1.2, -3.4, 5.6);
        AssertEqual(direct.TransformPoint(p), fromAxis.TransformPoint(p));
    }

    [Fact]
    public void Multiplication_AppliesRightFactorFirst()
    {
        var rotate = Matrix4d.CreateRotationZ(Math.PI / 2);
        var translate = Matrix4d.CreateTranslation(new Vector3d(10, 0, 0));

        // (translate * rotate): rotate first, then translate.
        var m = translate * rotate;
        AssertEqual(new Vector3d(10, 1, 0), m.TransformPoint(new Vector3d(1, 0, 0)));

        // (rotate * translate): translate first, then rotate.
        m = rotate * translate;
        AssertEqual(new Vector3d(0, 11, 0), m.TransformPoint(new Vector3d(1, 0, 0)));
    }

    [Fact]
    public void Inverse_RoundTripsRandomAffineMatrices()
    {
        var rng = new Random(1234);
        for (int trial = 0; trial < 50; trial++)
        {
            var m = Matrix4d.CreateTranslation(RandomVector(rng, 10))
                  * Matrix4d.CreateFromAxisAngle(RandomVector(rng, 1).Normalized(), rng.NextDouble() * Math.PI)
                  * Matrix4d.CreateScale(new Vector3d(
                        1 + rng.NextDouble(), 1 + rng.NextDouble(), 1 + rng.NextDouble()));

            Assert.True(m.TryInvert(out var inv));

            var p = RandomVector(rng, 100);
            AssertEqual(p, inv.TransformPoint(m.TransformPoint(p)), 1e-8);
        }
    }

    [Fact]
    public void TryInvert_FailsForSingularMatrix()
    {
        var singular = Matrix4d.CreateScale(new Vector3d(1, 0, 1));
        Assert.False(singular.TryInvert(out _));
    }

    [Fact]
    public void Determinant_OfScaleMatrix()
    {
        var m = Matrix4d.CreateScale(new Vector3d(2, 3, 4));
        Assert.Equal(24, m.Determinant, Precision);
        Assert.Equal(1, Matrix4d.Identity.Determinant, Precision);
        Assert.Equal(1, Matrix4d.CreateRotationX(1.23).Determinant, Precision);
    }

    [Fact]
    public void Transposed_SwapsRowsAndColumns()
    {
        var m = new Matrix4d(
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16);
        var t = m.Transposed();
        Assert.Equal(2, t.M21);
        Assert.Equal(5, t.M12);
        Assert.Equal(12, t.M43);
        Assert.Equal(m, t.Transposed());
    }

    private static Vector3d RandomVector(Random rng, double scale) => new(
        (rng.NextDouble() * 2 - 1) * scale,
        (rng.NextDouble() * 2 - 1) * scale,
        (rng.NextDouble() * 2 - 1) * scale);
}
