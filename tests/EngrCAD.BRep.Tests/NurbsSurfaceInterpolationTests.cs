using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class NurbsSurfaceInterpolationTests
{
    /// <summary>
    /// The averaged chord-length parameters the interpolation contract documents
    /// (The NURBS Book eqn 9.7), reproduced so the pass-through property can be asserted
    /// exactly — the same pattern the curve tests use for their chord parameters. Assumes
    /// a well-formed grid (every column and row has non-zero length).
    /// </summary>
    private static (double[] u, double[] v) AveragedParameters(Vector3d[,] q)
    {
        int nu = q.GetLength(0), nv = q.GetLength(1);
        var u = new double[nu];
        for (int j = 0; j < nv; j++)
        {
            double total = 0;
            var line = new double[nu];
            for (int i = 1; i < nu; i++) { total += q[i, j].DistanceTo(q[i - 1, j]); line[i] = total; }
            for (int i = 1; i < nu - 1; i++) u[i] += line[i] / total;
        }
        u[0] = 0; u[nu - 1] = 1;
        for (int i = 1; i < nu - 1; i++) u[i] /= nv;

        var v = new double[nv];
        for (int i = 0; i < nu; i++)
        {
            double total = 0;
            var line = new double[nv];
            for (int j = 1; j < nv; j++) { total += q[i, j].DistanceTo(q[i, j - 1]); line[j] = total; }
            for (int j = 1; j < nv - 1; j++) v[j] += line[j] / total;
        }
        v[0] = 0; v[nv - 1] = 1;
        for (int j = 1; j < nv - 1; j++) v[j] /= nu;
        return (u, v);
    }

    private static Vector3d[,] SaddleGrid()
    {
        // Non-uniform spacing in both directions, a genuinely non-planar height field.
        double[] xs = [0, 1, 2.3, 3.5, 5];
        double[] ys = [0, 1.2, 2, 3.4];
        var q = new Vector3d[xs.Length, ys.Length];
        for (int i = 0; i < xs.Length; i++)
            for (int j = 0; j < ys.Length; j++)
                q[i, j] = new Vector3d(xs[i], ys[j], 0.5 * Math.Sin(0.6 * xs[i]) * Math.Cos(0.5 * ys[j]));
        return q;
    }

    [Fact]
    public void InterpolatePoints_PassesThroughEveryGridPoint()
    {
        var q = SaddleGrid();
        var surface = NurbsSurface.InterpolatePoints(q);
        var (u, v) = AveragedParameters(q);

        Assert.Equal(3, surface.DegreeU);
        Assert.Equal(3, surface.DegreeV);
        // Cubic × cubic: two natural-end control points per direction.
        Assert.Equal(q.GetLength(0) + 2, surface.ControlPoints.GetLength(0));
        Assert.Equal(q.GetLength(1) + 2, surface.ControlPoints.GetLength(1));

        for (int i = 0; i < q.GetLength(0); i++)
            for (int j = 0; j < q.GetLength(1); j++)
            {
                double error = surface.PointAt(u[i], v[j]).DistanceTo(q[i, j]);
                Assert.True(error < 1e-9, $"grid point ({i},{j}) missed by {error:E3}");
            }
    }

    [Fact]
    public void InterpolatePoints_CornersAreTheCornerPoints()
    {
        var q = SaddleGrid();
        var surface = NurbsSurface.InterpolatePoints(q);
        int nu = q.GetLength(0) - 1, nv = q.GetLength(1) - 1;
        double u0 = surface.DomainU.Start, u1 = surface.DomainU.End;
        double v0 = surface.DomainV.Start, v1 = surface.DomainV.End;

        Assert.True(surface.PointAt(u0, v0).DistanceTo(q[0, 0]) < 1e-12);
        Assert.True(surface.PointAt(u1, v0).DistanceTo(q[nu, 0]) < 1e-12);
        Assert.True(surface.PointAt(u0, v1).DistanceTo(q[0, nv]) < 1e-12);
        Assert.True(surface.PointAt(u1, v1).DistanceTo(q[nu, nv]) < 1e-12);
    }

    [Fact]
    public void InterpolatePoints_DegreeOneInU()
    {
        // Two rows in u, five columns in v: a straight ruling in u, a natural cubic in v.
        var q = new Vector3d[2, 5];
        double[] ys = [0, 1, 2.4, 3, 4.5];
        for (int j = 0; j < 5; j++)
        {
            q[0, j] = new Vector3d(0, ys[j], 0.3 * ys[j]);
            q[1, j] = new Vector3d(3, ys[j], 1 + 0.2 * ys[j] * ys[j]);
        }
        var surface = NurbsSurface.InterpolatePoints(q);
        Assert.Equal(1, surface.DegreeU);
        Assert.Equal(3, surface.DegreeV);
        Assert.Equal(2, surface.ControlPoints.GetLength(0));
        Assert.Equal(7, surface.ControlPoints.GetLength(1));

        var (u, v) = AveragedParameters(q);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 5; j++)
                Assert.True(surface.PointAt(u[i], v[j]).DistanceTo(q[i, j]) < 1e-9);

        // Along u the surface is a straight lerp of its two rows at every v.
        var mid = surface.PointAt(0.5, v[2]);
        var expected = (q[0, 2] + q[1, 2]) * 0.5;
        Assert.True(mid.DistanceTo(expected) < 1e-9);
    }

    [Fact]
    public void InterpolatePoints_TwoByTwo_IsTheBilinearPatch()
    {
        var q = new Vector3d[2, 2]
        {
            { (0, 0, 0), (0, 2, 1) },
            { (3, 0, 0.5), (3, 2, 2) },
        };
        var surface = NurbsSurface.InterpolatePoints(q);
        Assert.Equal(1, surface.DegreeU);
        Assert.Equal(1, surface.DegreeV);

        // A bilinear (degree 1 × 1) patch reproduces bilinear interpolation exactly.
        for (double s = 0; s <= 1.0001; s += 0.25)
            for (double t = 0; t <= 1.0001; t += 0.25)
            {
                var bilinear =
                    q[0, 0] * ((1 - s) * (1 - t)) + q[0, 1] * ((1 - s) * t) +
                    q[1, 0] * (s * (1 - t)) + q[1, 1] * (s * t);
                Assert.True(surface.PointAt(s, t).DistanceTo(bilinear) < 1e-12);
            }
    }

    [Fact]
    public void InterpolatePoints_CoplanarGrid_StaysOnThePlane()
    {
        // Every control point is an affine combination of coplanar data, so the whole
        // surface lies on the plane — an INTERIOR check that pass-through alone cannot make.
        Vector3d Plane(double x, double y) => new(x, y, 0.3 * x - 0.2 * y + 1.5);
        double[] xs = [-1, 0.5, 2, 3.7, 5];
        double[] ys = [0, 1.1, 2.5, 4];
        var q = new Vector3d[xs.Length, ys.Length];
        for (int i = 0; i < xs.Length; i++)
            for (int j = 0; j < ys.Length; j++)
                q[i, j] = Plane(xs[i], ys[j]);
        var surface = NurbsSurface.InterpolatePoints(q);

        for (int i = 0; i <= 30; i++)
            for (int j = 0; j <= 30; j++)
            {
                var p = surface.PointAt(surface.DomainU.ParameterAt(i / 30.0), surface.DomainV.ParameterAt(j / 30.0));
                double onPlane = 0.3 * p.X - 0.2 * p.Y + 1.5;
                Assert.True(Math.Abs(p.Z - onPlane) < 1e-9, $"({i},{j}) off plane by {Math.Abs(p.Z - onPlane):E3}");
            }
    }

    [Fact]
    public void InterpolatePoints_SmoothField_InteriorDeviationIsSmall()
    {
        // A 7×6 grid of a smooth surface. Between grid points the bicubic interpolant is
        // O(h⁴) in the interior, degraded to O(h²) near the boundary by the natural end
        // conditions (measured max interior deviation ~1.3e-3; the bound sits ~2× above).
        Vector3d Field(double x, double y) => new(x, y, 0.6 * Math.Sin(0.7 * x) * Math.Cos(0.5 * y));
        const int nu = 7, nv = 6;
        var q = new Vector3d[nu, nv];
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
                q[i, j] = Field(4.0 * i / (nu - 1), 3.0 * j / (nv - 1));
        var surface = NurbsSurface.InterpolatePoints(q);

        double maxInterior = 0;
        for (int i = 0; i <= 60; i++)
            for (int j = 0; j <= 60; j++)
            {
                double su = i / 60.0, sv = j / 60.0;
                var p = surface.PointAt(surface.DomainU.ParameterAt(su), surface.DomainV.ParameterAt(sv));
                double trueZ = 0.6 * Math.Sin(0.7 * p.X) * Math.Cos(0.5 * p.Y);
                double dev = Math.Abs(p.Z - trueZ);
                if (su is >= 0.25 and <= 0.75 && sv is >= 0.25 and <= 0.75)
                    maxInterior = Math.Max(maxInterior, dev);
            }
        Assert.True(maxInterior < 3e-3, $"interior deviation {maxInterior:E3}");
    }

    [Fact]
    public void InterpolatePoints_ValidatesInputs()
    {
        // Too few points in a direction.
        Assert.Throws<ArgumentException>(() =>
            NurbsSurface.InterpolatePoints(new Vector3d[1, 3]));

        // A coincident row of points makes the v parameters non-monotone.
        var coincidentRow = new Vector3d[3, 3]
        {
            { (0, 0, 0), (0, 0, 0), (0, 0, 0) }, // every point of row 0 identical to row 1's? no — same j
            { (1, 0, 0), (1, 1, 0), (1, 2, 0) },
            { (2, 0, 0), (2, 1, 0), (2, 2, 0) },
        };
        // Row 0 collapses to a single point: its three columns coincide in v, so the v
        // direction cannot be parameterized from that row, but the other rows are fine —
        // this instead makes the v spacing come only from rows 1 and 2, still valid.
        var ok = NurbsSurface.InterpolatePoints(coincidentRow);
        Assert.NotNull(ok);

        // A whole column collapsed to one point in u — every column identical in u.
        var flatU = new Vector3d[3, 2]
        {
            { (0, 0, 0), (5, 0, 0) },
            { (0, 0, 0), (5, 0, 0) },
            { (0, 0, 0), (5, 0, 0) },
        };
        Assert.Throws<ArgumentException>(() => NurbsSurface.InterpolatePoints(flatU));
    }
}
