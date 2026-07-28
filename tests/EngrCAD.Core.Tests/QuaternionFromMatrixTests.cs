using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// <see cref="Quaterniond.FromRotationMatrix"/> — Shepperd's branch-on-the-largest
/// extraction. The tests drive all four branches deliberately: the trace branch (small
/// angles), and each diagonal branch (near-half-turn rotations about each axis, where
/// the trace approaches −1 and the naive w = √(trace+1)/2 form loses everything).
/// </summary>
public class QuaternionFromMatrixTests
{
    private static void AssertRoundTrips(in Vector3d axis, double angle)
    {
        var q = Quaterniond.FromAxisAngle(axis.Normalized(), angle);
        var back = Quaterniond.FromRotationMatrix(q.ToMatrix());
        // q and −q are the same rotation; compare via |dot| = 1.
        Assert.Equal(1.0, Math.Abs(q.Dot(back)), 12);
    }

    [Theory]
    // Trace branch: small and moderate angles.
    [InlineData(1, 0, 0, 0.001)]
    [InlineData(0, 1, 0, 0.5)]
    [InlineData(0, 0, 1, 1.0)]
    [InlineData(1, 2, 3, 0.75)]
    // Diagonal branches: near-half-turns about each axis (trace near −1).
    [InlineData(1, 0, 0, 3.14)]
    [InlineData(0, 1, 0, 3.14)]
    [InlineData(0, 0, 1, 3.14)]
    [InlineData(1, 1, 0, 3.0)]
    [InlineData(1, -2, 5, 2.9)]
    // Exact half turns (the branch divisors' best case, the trace form's worst).
    [InlineData(1, 0, 0, Math.PI)]
    [InlineData(0, 1, 0, Math.PI)]
    [InlineData(0, 0, 1, Math.PI)]
    public void RoundTripsThroughToMatrix(double x, double y, double z, double angle) =>
        AssertRoundTrips(new Vector3d(x, y, z), angle);

    [Fact]
    public void IdentityMatrixGivesIdentityQuaternion()
    {
        var q = Quaterniond.FromRotationMatrix(Matrix4d.Identity);
        Assert.Equal(1.0, Math.Abs(q.W), 15);
    }

    [Fact]
    public void RotatesVectorsExactlyLikeTheMatrix()
    {
        var q = Quaterniond.FromAxisAngle(new Vector3d(2, -1, 4).Normalized(), 1.234);
        var m = q.ToMatrix();
        var extracted = Quaterniond.FromRotationMatrix(m);
        var v = new Vector3d(0.3, -2.5, 1.7);
        var byMatrix = m.TransformVector(v);
        var byQuaternion = extracted.Rotate(v);
        Assert.Equal(byMatrix.X, byQuaternion.X, 12);
        Assert.Equal(byMatrix.Y, byQuaternion.Y, 12);
        Assert.Equal(byMatrix.Z, byQuaternion.Z, 12);
    }

    [Fact]
    public void DeterministicSweepOverOrientations()
    {
        // A structured sweep (not random): axes over a coarse sphere grid, angles over
        // the full turn, all branches crossed repeatedly.
        for (int i = 0; i < 8; i++)
        {
            for (int j = 1; j < 8; j++)
            {
                double azimuth = i * Math.PI / 4;
                double elevation = (j - 4) * Math.PI / 9;
                var axis = new Vector3d(
                    Math.Cos(elevation) * Math.Cos(azimuth),
                    Math.Cos(elevation) * Math.Sin(azimuth),
                    Math.Sin(elevation));
                for (int k = 1; k < 12; k++)
                    AssertRoundTrips(axis, k * Math.PI / 6.1);
            }
        }
    }
}
