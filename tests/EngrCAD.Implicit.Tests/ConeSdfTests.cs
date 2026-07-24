using EngrCAD.Core;
using EngrCAD.Implicit;
using Xunit;

namespace EngrCAD.Implicit.Tests;

public class ConeSdfTests
{
    /// <summary>Exact 2D signed distance to the frustum cross-section in the (ρ, z)
    /// half-plane: the ground truth the 3D SDF must reproduce by symmetry.</summary>
    private static double CrossSectionDistance(double r1, double r2, double h, double rho, double z)
    {
        // Boundary polygon of the cross-section (ρ ≥ 0): axis is not a boundary.
        var vertices = new[]
        {
            (rho: 0.0, z: -h / 2), (rho: r1, z: -h / 2), (rho: r2, z: h / 2), (rho: 0.0, z: h / 2),
        };
        double best = double.PositiveInfinity;
        for (int i = 0; i + 1 < vertices.Length; i++)
        {
            var (ax, ay) = vertices[i];
            var (bx, by) = vertices[i + 1];
            double abx = bx - ax, aby = by - ay;
            double t = Math.Clamp(((rho - ax) * abx + (z - ay) * aby) / (abx * abx + aby * aby), 0, 1);
            double dx = rho - (ax + abx * t), dy = z - (ay + aby * t);
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }

        // Inside test: below the slant line and between the caps.
        bool inside = Math.Abs(z) < h / 2 && rho < r1 + (r2 - r1) * (z / h + 0.5);
        return inside ? -best : best;
    }

    [Fact]
    public void EqualRadii_MatchesCylinderExactly()
    {
        var cone = Sdf.Cone(1, 1, 2);
        var cylinder = Sdf.Cylinder(1, 2);
        var random = new Random(42);
        for (int i = 0; i < 200; i++)
        {
            var p = new Vector3d(
                (random.NextDouble() - 0.5) * 6, (random.NextDouble() - 0.5) * 6, (random.NextDouble() - 0.5) * 6);
            Assert.Equal(cylinder.Evaluate(p), cone.Evaluate(p), 12);
        }
    }

    [Fact]
    public void Frustum_MatchesExactCrossSectionDistance()
    {
        const double r1 = 1.0, r2 = 0.5, h = 2.0;
        var cone = Sdf.Cone(r1, r2, h);
        var random = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            var p = new Vector3d(
                (random.NextDouble() - 0.5) * 5, (random.NextDouble() - 0.5) * 5, (random.NextDouble() - 0.5) * 5);
            double rho = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            Assert.Equal(CrossSectionDistance(r1, r2, h, rho, p.Z), cone.Evaluate(p), 12);
        }
    }

    [Fact]
    public void ApexCone_DistanceAboveApexIsExact()
    {
        var cone = Sdf.Cone(1, 0, 2); // apex at z = +1
        Assert.Equal(1.0, cone.Evaluate(new Vector3d(0, 0, 2)), 12);
        Assert.Equal(0.5, cone.Evaluate(new Vector3d(0, 0, -1.5)), 12);
    }

    [Fact]
    public void Distance_IsOneLipschitz()
    {
        // An exact SDF never changes faster than distance in space.
        var cone = Sdf.Cone(1.5, 0.5, 2);
        var random = new Random(11);
        for (int i = 0; i < 500; i++)
        {
            var p = new Vector3d(
                (random.NextDouble() - 0.5) * 6, (random.NextDouble() - 0.5) * 6, (random.NextDouble() - 0.5) * 6);
            var q = new Vector3d(
                (random.NextDouble() - 0.5) * 6, (random.NextDouble() - 0.5) * 6, (random.NextDouble() - 0.5) * 6);
            Assert.True(
                Math.Abs(cone.Evaluate(p) - cone.Evaluate(q)) <= p.DistanceTo(q) + 1e-12,
                $"SDF changed faster than distance between {p} and {q}");
        }
    }

    [Fact]
    public void InvalidInputs_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Cone(-1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Cone(1, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sdf.Cone(1, 1, 0));
    }
}
