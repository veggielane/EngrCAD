using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

public class QuaterniondTests
{
    private const double Precision = 1e-9;

    private static void AssertEqual(in Vector3d expected, in Vector3d actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Z, actual.Z, Precision);
    }

    [Fact]
    public void Identity_LeavesVectorsUnchanged()
    {
        var v = new Vector3d(1, -2, 3);
        AssertEqual(v, Quaterniond.Identity.Rotate(v));
    }

    [Fact]
    public void AxisAngle_QuarterTurnAboutZ()
    {
        var q = Quaterniond.FromAxisAngle(Vector3d.UnitZ, Math.PI / 2);
        AssertEqual(new Vector3d(0, 1, 0), q.Rotate(new Vector3d(1, 0, 0)));
    }

    [Fact]
    public void Rotate_MatchesMatrixRotation()
    {
        var rng = new Random(42);
        for (int trial = 0; trial < 50; trial++)
        {
            var axis = new Vector3d(
                rng.NextDouble() * 2 - 1,
                rng.NextDouble() * 2 - 1,
                rng.NextDouble() * 2 - 1).Normalized();
            double angle = rng.NextDouble() * 2 * Math.PI;
            var v = new Vector3d(rng.NextDouble() * 10, rng.NextDouble() * 10, rng.NextDouble() * 10);

            var q = Quaterniond.FromAxisAngle(axis, angle);
            var m = Matrix4d.CreateFromAxisAngle(axis, angle);

            AssertEqual(m.TransformPoint(v), q.Rotate(v));
            AssertEqual(m.TransformPoint(v), q.ToMatrix().TransformPoint(v));
        }
    }

    [Fact]
    public void Multiplication_ComposesLikeMatrices()
    {
        var qa = Quaterniond.FromAxisAngle(Vector3d.UnitX, 0.4);
        var qb = Quaterniond.FromAxisAngle(Vector3d.UnitZ, 1.1);
        var v = new Vector3d(1, 2, 3);

        // (qa * qb) applies qb first, matching matrix convention.
        AssertEqual(qa.Rotate(qb.Rotate(v)), (qa * qb).Rotate(v));
    }

    [Fact]
    public void Conjugate_UndoesRotation()
    {
        var q = Quaterniond.FromAxisAngle(new Vector3d(1, 2, 3).Normalized(), 0.9);
        var v = new Vector3d(4, 5, 6);
        AssertEqual(v, q.Conjugate.Rotate(q.Rotate(v)));
    }

    [Fact]
    public void Slerp_EndpointsAndMidpoint()
    {
        var a = Quaterniond.Identity;
        var b = Quaterniond.FromAxisAngle(Vector3d.UnitZ, Math.PI / 2);

        var v = new Vector3d(1, 0, 0);
        AssertEqual(a.Rotate(v), Quaterniond.Slerp(a, b, 0).Rotate(v));
        AssertEqual(b.Rotate(v), Quaterniond.Slerp(a, b, 1).Rotate(v));

        // Midpoint is a 45° rotation.
        var expected = Quaterniond.FromAxisAngle(Vector3d.UnitZ, Math.PI / 4);
        AssertEqual(expected.Rotate(v), Quaterniond.Slerp(a, b, 0.5).Rotate(v));
    }

    [Fact]
    public void Normalized_ProducesUnitLength()
    {
        var q = new Quaterniond(1, 2, 3, 4).Normalized();
        Assert.Equal(1.0, q.Length, Precision);
    }
}
