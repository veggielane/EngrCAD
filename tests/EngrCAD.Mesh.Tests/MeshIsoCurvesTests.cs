using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Mesh.Tests;

public class MeshIsoCurvesTests
{
    [Fact]
    public void PlaneGrid_LevelBetweenColumns_GivesOneStraightOpenChain()
    {
        const int size = 8;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        var curves = MeshIsoCurves.Extract(grid, p => p.X, level: 3.5);

        var curve = Assert.Single(curves);
        Assert.False(curve.IsClosed);
        foreach (var p in curve.Points)
            Assert.Equal(3.5, p.X, 12);

        // Spans the full grid height, ordered monotonically in y.
        Assert.Equal(size, Math.Abs(curve.Points[^1].Y - curve.Points[0].Y), 12);
        double sign = Math.Sign(curve.Points[^1].Y - curve.Points[0].Y);
        for (int i = 1; i < curve.Points.Count; i++)
            Assert.True(sign * (curve.Points[i].Y - curve.Points[i - 1].Y) >= 0);
    }

    [Fact]
    public void PlaneGrid_ContourOrientationKeepsBelowRegionOnTheLeft()
    {
        // Grid faces are CCW seen from +Z. Walking +y, the left side (rotate the
        // direction 90 degrees CCW) is -x — the below-level side for level x = 3.5 —
        // so the contour must run in +y.
        const int size = 6;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        var curve = Assert.Single(MeshIsoCurves.Extract(grid, p => p.X, level: 3.5));
        Assert.True(curve.Points[^1].Y > curve.Points[0].Y);
    }

    [Fact]
    public void Sphere_LatitudeContourIsOneClosedLoop()
    {
        const double radius = 5, level = 0.5;
        var sphere = MeshPrimitives.UvSphere(radius, 48, 24);
        var curves = MeshIsoCurves.Extract(sphere, p => p.Z, level);

        var loop = Assert.Single(curves);
        Assert.True(loop.IsClosed);
        Assert.True(loop.Points.Count >= 40);

        double expectedRadius = Math.Sqrt(radius * radius - level * level);
        foreach (var p in loop.Points)
        {
            Assert.Equal(level, p.Z, 12); // linear interpolation of z is exact in z
            // Points lie on chords of the sphere, so the ring radius is slightly inside.
            Assert.InRange(Math.Sqrt(p.X * p.X + p.Y * p.Y), expectedRadius * 0.99, expectedRadius * 1.0000001);
        }

        // Length approximates the latitude circle from inside.
        Assert.InRange(loop.Length, 2 * Math.PI * expectedRadius * 0.98, 2 * Math.PI * expectedRadius * 1.0000001);
    }

    [Fact]
    public void LevelThroughVertexColumn_StillCoversTheColumn()
    {
        // The strict inside rule classifies at-level vertices as outside; degenerate
        // zero-length segments are dropped and the surviving segments run along the
        // vertex column itself.
        const int size = 6;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        var curves = MeshIsoCurves.Extract(grid, p => p.X, level: 3.0);

        Assert.NotEmpty(curves);
        double totalLength = curves.Sum(c => c.Length);
        Assert.Equal(size, totalLength, 9);
        foreach (var p in curves.SelectMany(c => c.Points))
            Assert.Equal(3.0, p.X, 12);
    }

    [Fact]
    public void TwoBlobs_GiveTwoClosedLoops()
    {
        const int size = 16;
        var grid = LaplacianSmootherTests.PlaneGrid(size);
        var a = new Vector3d(4, 8, 0);
        var b = new Vector3d(12, 8, 0);
        double Field(Vector3d p) => Math.Min((p - a).Length, (p - b).Length);
        var curves = MeshIsoCurves.Extract(grid, Field, level: 2.2);

        Assert.Equal(2, curves.Count);
        Assert.All(curves, c => Assert.True(c.IsClosed));
        // The field is curved, so edge-linear interpolation carries O(h²·curvature)
        // error — the same bound SdfContours documents for its grid.
        foreach (var p in curves.SelectMany(c => c.Points))
            Assert.Equal(2.2, Field(p), 1);
    }

    [Fact]
    public void SharedEndpoints_AreBitIdentical_SoLoopsChainExactly()
    {
        // The chaining is combinatorial, but the bit-identical endpoint contract is
        // what downstream consumers (loops by exact equality, as SdfContours documents)
        // rely on: walking a closed loop, consecutive points must never repeat and the
        // loop must close combinatorially without any tolerance having been applied.
        var sphere = MeshPrimitives.UvSphere(3, 24, 12);
        var loop = Assert.Single(MeshIsoCurves.Extract(sphere, p => p.Z, 0.25));
        Assert.True(loop.IsClosed);
        for (int i = 1; i < loop.Points.Count; i++)
            Assert.NotEqual(loop.Points[i - 1], loop.Points[i]);
        Assert.NotEqual(loop.Points[0], loop.Points[^1]);
    }

    [Fact]
    public void ExplicitValues_OverloadAgreesWithFieldOverload()
    {
        var sphere = MeshPrimitives.UvSphere(2, 16, 8);
        var values = new double[sphere.VertexCount];
        for (int v = 0; v < values.Length; v++)
            values[v] = sphere.GetPosition(v).Z;
        var fromValues = MeshIsoCurves.Extract(sphere, values, 0.3);
        var fromField = MeshIsoCurves.Extract(sphere, p => p.Z, 0.3);
        Assert.Equal(fromField.Count, fromValues.Count);
        for (int c = 0; c < fromField.Count; c++)
            Assert.Equal(fromField[c].Points, fromValues[c].Points); // bitwise
    }

    [Fact]
    public void NoCrossing_ReturnsEmpty()
    {
        var sphere = MeshPrimitives.UvSphere(1, 12, 6);
        Assert.Empty(MeshIsoCurves.Extract(sphere, p => p.Z, 5.0));
    }

    [Fact]
    public void Extract_IsDeterministic()
    {
        var sphere = MeshPrimitives.UvSphere(4, 32, 16);
        var a = MeshIsoCurves.Extract(sphere, p => p.X + 0.3 * p.Z, 0.7);
        var b = MeshIsoCurves.Extract(sphere, p => p.X + 0.3 * p.Z, 0.7);
        Assert.Equal(a.Count, b.Count);
        for (int c = 0; c < a.Count; c++)
        {
            Assert.Equal(a[c].IsClosed, b[c].IsClosed);
            Assert.Equal(a[c].Points, b[c].Points); // bitwise
        }
    }

    [Fact]
    public void Extract_RejectsWrongValueCount()
    {
        var grid = LaplacianSmootherTests.PlaneGrid(3);
        Assert.Throws<ArgumentException>(() => MeshIsoCurves.Extract(grid, new double[3], 0));
    }
}
