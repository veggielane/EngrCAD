using Xunit;

namespace EngrCAD.Core.Tests;

public class Fitting3dTests
{
    [Fact]
    public void FitPlane_ExactPlanePoints_RecoverThePlane()
    {
        // A grid on a known tilted plane.
        var frame = Frame3d.FromNormal((1, 2, 3), new Vector3d(1, 2, 2).Normalized());
        var points = new List<Vector3d>();
        for (int u = -3; u <= 3; u++)
        {
            for (int v = -2; v <= 2; v++)
                points.Add(frame.ToWorld(new Vector3d(u * 0.7, v * 1.3, 0)));
        }

        var fit = Fitting3d.FitPlane(points);

        Assert.True(fit.Z.IsParallelTo(frame.Z, Tolerance.Default), $"normal {fit.Z}");
        // Origin (centroid) lies on the plane.
        Assert.Equal(0.0, Math.Abs((fit.Origin - frame.Origin).Dot(frame.Z)), 12);
        // Frame is right-handed and orthonormal (Frame3d guarantees it, but assert anyway).
        Assert.True(fit.X.Cross(fit.Y).AreEqual(fit.Z, Tolerance.Default));
    }

    [Fact]
    public void FitPlane_NoisyPoints_NormalWithinNoiseBound()
    {
        // z = 0 plane, extent ~10, noise ±0.02: the PCA normal tilt is bounded by
        // roughly noise/extent — allow a generous 0.02 rad.
        var random = new Random(11);
        var points = new List<Vector3d>();
        for (int i = 0; i < 400; i++)
        {
            points.Add((
                random.NextDouble() * 10 - 5,
                random.NextDouble() * 10 - 5,
                (random.NextDouble() - 0.5) * 0.04));
        }

        var fit = Fitting3d.FitPlane(points);
        Assert.True(fit.Z.AngleTo(Vector3d.UnitZ) < 0.02 || fit.Z.AngleTo(-Vector3d.UnitZ) < 0.02,
            $"normal {fit.Z} drifted");
        Assert.True(Math.Abs(fit.Origin.Z) < 0.02);
    }

    [Fact]
    public void FitPlane_XAxisFollowsTheDominantSpread()
    {
        // Points stretched 10:1 along a known in-plane direction.
        var points = new List<Vector3d>();
        for (int i = -10; i <= 10; i++)
        {
            points.Add((i, 0.1 * ((i * 7) % 3), 0));
            points.Add((i, -0.1 * ((i * 5) % 3), 0));
        }
        var fit = Fitting3d.FitPlane(points);
        Assert.True(fit.X.IsParallelTo(Vector3d.UnitX, new Tolerance(1e-9, 0.05)),
            $"dominant axis {fit.X}");
    }

    [Fact]
    public void FitPlane_DegenerateInputs_Throw()
    {
        Assert.Throws<ArgumentException>(() => Fitting3d.FitPlane([]));
        Assert.Throws<ArgumentException>(() => Fitting3d.FitPlane([(1, 1, 1), (1, 1, 1)]));
        Assert.Throws<ArgumentException>(() =>
            Fitting3d.FitPlane([(0, 0, 0), (1, 1, 0), (2, 2, 0), (3, 3, 0)]));
    }

    [Fact]
    public void FitBox_RecoversARotatedBox()
    {
        // A 4×2×1 box lattice, rotated rigidly: PCA axes align with the box axes
        // (distinct extents => distinct eigenvalues), so the fit is the exact box.
        var rotation = Frame3d.FromXY((0.5, -1, 2),
            new Vector3d(1, 1, 0).Normalized(),
            new Vector3d(-1, 1, 1).Normalized());
        var points = new List<Vector3d>();
        for (int x = 0; x <= 8; x++)
            for (int y = 0; y <= 4; y++)
                for (int z = 0; z <= 2; z++)
                    points.Add(rotation.ToWorld(new Vector3d(x * 0.5 - 2, y * 0.5 - 1, z * 0.5 - 0.5)));

        var box = Fitting3d.FitBox(points);

        Assert.Equal(4 * 2 * 1, box.Volume, 9);
        var sorted = new[] { box.HalfExtents.X, box.HalfExtents.Y, box.HalfExtents.Z }
            .OrderByDescending(e => e).ToArray();
        Assert.Equal(2.0, sorted[0], 9);
        Assert.Equal(1.0, sorted[1], 9);
        Assert.Equal(0.5, sorted[2], 9);
        Assert.True(box.Center.AreEqual(rotation.Origin, Tolerance.Default));
        Assert.All(points, p => Assert.True(box.Contains(p, Tolerance.Default)));
    }

    [Fact]
    public void FitBox_ContainsEveryPointOnRandomClouds()
    {
        var random = new Random(23);
        for (int trial = 0; trial < 10; trial++)
        {
            var points = new List<Vector3d>();
            for (int i = 0; i < 200; i++)
                points.Add((random.NextDouble() * 4, random.NextDouble() * 2 - 3, random.NextDouble()));
            var box = Fitting3d.FitBox(points);
            Assert.All(points, p => Assert.True(box.Contains(p, Tolerance.Default)));
            Assert.True(box.Volume > 0);

            // Corners reconstruct the extents.
            var c0 = box.Corner(0);
            var c7 = box.Corner(7);
            Assert.Equal((c0 - c7).Length, 2 * box.HalfExtents.Length, 9);
        }
    }

    [Fact]
    public void FitBox_FlatCloud_GetsZeroThickness()
    {
        var points = new List<Vector3d>();
        for (int i = 0; i < 10; i++)
            points.Add((i, (i * 3) % 5, 0));
        var box = Fitting3d.FitBox(points);
        double thin = Math.Min(box.HalfExtents.X, Math.Min(box.HalfExtents.Y, box.HalfExtents.Z));
        Assert.Equal(0.0, thin, 12);
        Assert.All(points, p => Assert.True(box.Contains(p, Tolerance.Default)));
    }
}
