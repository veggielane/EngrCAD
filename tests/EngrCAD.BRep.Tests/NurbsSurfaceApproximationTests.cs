using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class NurbsSurfaceApproximationTests
{
    /// <summary>Averaged chord-length parameters (The NURBS Book eqn 9.7), reproduced so the
    /// exact fits can be checked at the grid parameters; assumes a well-formed grid.</summary>
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

    private static Vector3d[,] SampleField(Func<double, double, double> f, int nu, int nv, double xMax, double yMax)
    {
        var q = new Vector3d[nu, nv];
        for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                double x = xMax * i / (nu - 1), y = yMax * j / (nv - 1);
                q[i, j] = new Vector3d(x, y, f(x, y));
            }
        return q;
    }

    [Fact]
    public void Approximate_FullControlCount_ReproducesEveryGridPoint()
    {
        // A control net as large as the data is a determined system: the least-squares
        // residual is exactly zero, so the fit interpolates every grid point — this is
        // the averaging-knot interpolation, valid for ANY well-spaced data.
        var q = SampleField((x, y) => 0.6 * Math.Sin(0.8 * x) * Math.Cos(0.6 * y), 8, 7, 4.0, 3.0);
        var surface = NurbsSurface.Approximate(q, 8, 7);
        var (u, v) = AveragedParameters(q);

        double worst = 0;
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 7; j++)
                worst = Math.Max(worst, surface.PointAt(u[i], v[j]).DistanceTo(q[i, j]));
        Assert.True(worst < 1e-9, $"full-count fit missed a grid point by {worst:E3}");
    }

    [Fact]
    public void Approximate_CoarseNet_CoplanarDataStaysExactlyPlanar()
    {
        // Least squares is LINEAR in the data, so an affine relation among the coordinates
        // survives the fit: coplanar data gives a surface that lies exactly on the plane
        // everywhere, however coarse the net — an INTERIOR check, not just the grid.
        double[] xs = new double[10], ys = new double[8];
        var q = new Vector3d[10, 8];
        for (int i = 0; i < 10; i++)
            for (int j = 0; j < 8; j++)
            {
                double x = -1 + 6.0 * i / 9, y = 0.5 * j;
                q[i, j] = new Vector3d(x, y, 0.35 * x - 0.22 * y + 1.7);
            }
        var surface = NurbsSurface.Approximate(q, 4, 5);
        Assert.Equal(4, surface.ControlPoints.GetLength(0));
        Assert.Equal(5, surface.ControlPoints.GetLength(1));

        double worst = 0;
        for (int i = 0; i <= 40; i++)
            for (int j = 0; j <= 40; j++)
            {
                var p = surface.PointAt(surface.DomainU.ParameterAt(i / 40.0), surface.DomainV.ParameterAt(j / 40.0));
                worst = Math.Max(worst, Math.Abs(p.Z - (0.35 * p.X - 0.22 * p.Y + 1.7)));
            }
        Assert.True(worst < 1e-9, $"coarse fit left the plane by {worst:E3}");
    }

    [Fact]
    public void Approximate_CornersInterpolateExactly()
    {
        var q = SampleField((x, y) => 0.5 + 0.4 * Math.Sin(x) * Math.Sin(y), 11, 9, 4.0, 3.0);
        var surface = NurbsSurface.Approximate(q, 5, 4);
        int nu = 10, nv = 8;
        double u0 = surface.DomainU.Start, u1 = surface.DomainU.End;
        double v0 = surface.DomainV.Start, v1 = surface.DomainV.End;

        Assert.True(surface.PointAt(u0, v0).DistanceTo(q[0, 0]) < 1e-12);
        Assert.True(surface.PointAt(u1, v0).DistanceTo(q[nu, 0]) < 1e-12);
        Assert.True(surface.PointAt(u0, v1).DistanceTo(q[0, nv]) < 1e-12);
        Assert.True(surface.PointAt(u1, v1).DistanceTo(q[nu, nv]) < 1e-12);
    }

    [Fact]
    public void Approximate_RicherNet_FitsMoreTightly()
    {
        // A curved field the coarse net cannot represent: the grid-point residual falls as
        // the net grows (and reaches round-off at the full count, the determined case).
        var q = SampleField((x, y) => 0.6 * Math.Sin(0.9 * x) * Math.Cos(0.7 * y), 11, 9, 4.0, 3.0);
        var (u, v) = AveragedParameters(q);

        double Rms(int cu, int cv)
        {
            var s = NurbsSurface.Approximate(q, cu, cv);
            double sum = 0;
            int count = 0;
            for (int i = 0; i < 11; i++)
                for (int j = 0; j < 9; j++)
                {
                    double e = s.PointAt(u[i], v[j]).DistanceTo(q[i, j]);
                    sum += e * e;
                    count++;
                }
            return Math.Sqrt(sum / count);
        }

        double coarse = Rms(4, 4), medium = Rms(6, 5), fine = Rms(9, 7), full = Rms(11, 9);
        Assert.True(medium < coarse, $"6×5 rms {medium:E3} not below 4×4 {coarse:E3}");
        Assert.True(fine < medium, $"9×7 rms {fine:E3} not below 6×5 {medium:E3}");
        Assert.True(full < 1e-9, $"full-count rms {full:E3} not round-off");
    }

    [Fact]
    public void Approximate_ToTolerance_MeetsTheTolerance()
    {
        var q = SampleField((x, y) => 0.6 * Math.Sin(0.9 * x) * Math.Cos(0.7 * y), 12, 10, 4.0, 3.0);
        var (u, v) = AveragedParameters(q);

        double Worst(NurbsSurface s)
        {
            double worst = 0;
            for (int i = 0; i < 12; i++)
                for (int j = 0; j < 10; j++)
                    worst = Math.Max(worst, s.PointAt(u[i], v[j]).DistanceTo(q[i, j]));
            return worst;
        }

        foreach (double tol in new[] { 1e-1, 1e-2, 1e-3 })
        {
            var s = NurbsSurface.Approximate(q, tol);
            Assert.True(Worst(s) <= tol, $"tolerance {tol:E1} fit missed by {Worst(s):E3}");
        }

        // A tighter tolerance never uses a smaller net than a looser one.
        Assert.True(NurbsSurface.Approximate(q, 1e-3).ControlPoints.Length >=
                    NurbsSurface.Approximate(q, 1e-1).ControlPoints.Length);
    }

    [Fact]
    public void Approximate_ToTolerance_PlaneFitsAtTheMinimumNet()
    {
        var q = new Vector3d[9, 7];
        for (int i = 0; i < 9; i++)
            for (int j = 0; j < 7; j++)
            {
                double x = 0.5 * i, y = 0.4 * j;
                q[i, j] = new Vector3d(x, y, 0.25 * x - 0.1 * y + 2);
            }
        var s = NurbsSurface.Approximate(q, 1e-9);
        // A plane is in the smallest bicubic net's space, so it fits at 4×4 to round-off.
        Assert.Equal(4, s.ControlPoints.GetLength(0));
        Assert.Equal(4, s.ControlPoints.GetLength(1));
    }

    [Fact]
    public void Approximate_ToTolerance_Validates()
    {
        var q = SampleField((x, y) => x + y, 8, 7, 4.0, 3.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => NurbsSurface.Approximate(q, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NurbsSurface.Approximate(q, 1e-3, degreeU: 8));
    }

    [Fact]
    public void Approximate_ValidatesControlCounts()
    {
        var q = SampleField((x, y) => x + y, 8, 7, 4.0, 3.0);
        // Below degree + 1.
        Assert.Throws<ArgumentOutOfRangeException>(() => NurbsSurface.Approximate(q, 3, 5));
        // Above the point count.
        Assert.Throws<ArgumentOutOfRangeException>(() => NurbsSurface.Approximate(q, 9, 5));
        // Degree below 1.
        Assert.Throws<ArgumentOutOfRangeException>(() => NurbsSurface.Approximate(q, 5, 5, degreeU: 0));
        // Grid too small.
        Assert.Throws<ArgumentException>(() => NurbsSurface.Approximate(new Vector3d[1, 5], 1, 4));
    }
}
