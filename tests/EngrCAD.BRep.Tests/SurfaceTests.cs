using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class SurfaceTests
{
    [Fact]
    public void Plane_PointNormalAndProjection()
    {
        var plane = new PlaneSurface((1, 1, 1), Vector3d.UnitX, Vector3d.UnitY);
        Assert.Equal(new Vector3d(3, 4, 1), plane.PointAt(2, 3));
        Assert.Equal(Vector3d.UnitZ, plane.NormalAt(0, 0));
        Assert.Equal(new Vector2d(2, 3), plane.Project((3, 4, 1)));
    }

    [Fact]
    public void Cylinder_PointAndRadialNormal()
    {
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 2);
        Assert.True(cylinder.PointAt(0, 5).AreEqual((2, 0, 5), Tolerance.Default));
        Assert.True(cylinder.PointAt(Math.PI / 2, -1).AreEqual((0, 2, -1), new Tolerance(1e-9, 1e-9)));
        Assert.True(cylinder.NormalAt(Math.PI / 2, 3).AreEqual(Vector3d.UnitY, new Tolerance(1e-9, 1e-9)));
        Assert.Equal(Vector3d.UnitZ, cylinder.Axis);
    }

    [Fact]
    public void Sphere_PointsAtRadiusWithOutwardNormals()
    {
        var sphere = new SphereSurface((0, 0, 0), 3);
        Assert.True(sphere.PointAt(0, 0).AreEqual((3, 0, 0), Tolerance.Default));
        Assert.True(sphere.PointAt(0, Math.PI / 2).AreEqual((0, 0, 3), new Tolerance(1e-9, 1e-9)));

        var rng = new Random(3);
        for (int i = 0; i < 20; i++)
        {
            double u = rng.NextDouble() * 2 * Math.PI;
            double v = (rng.NextDouble() - 0.5) * Math.PI;
            var p = sphere.PointAt(u, v);
            Assert.Equal(3, p.Length, 12);
            Assert.True(sphere.NormalAt(u, v).AreEqual(p.Normalized(), new Tolerance(1e-9, 1e-9)));
        }
    }

    [Fact]
    public void NurbsSurface_BilinearPatchInterpolates()
    {
        var patch = new NurbsSurface(
            1, 1,
            new Vector3d[2, 2] { { (0, 0, 0), (0, 2, 0) }, { (2, 0, 0), (2, 2, 4) } },
            null,
            [0, 0, 1, 1], [0, 0, 1, 1]);

        Assert.True(patch.PointAt(0, 0).AreEqual((0, 0, 0), Tolerance.Default));
        Assert.True(patch.PointAt(1, 1).AreEqual((2, 2, 4), Tolerance.Default));
        Assert.True(patch.PointAt(0.5, 0.5).AreEqual((1, 1, 1), Tolerance.Default)); // corner average
    }

    [Fact]
    public void GenericNormal_FallsBackToFiniteDifferences()
    {
        // NurbsSurface has no exact-normal override; a flat patch's normal is still ±Z.
        var patch = new NurbsSurface(
            1, 1,
            new Vector3d[2, 2] { { (0, 0, 0), (0, 1, 0) }, { (1, 0, 0), (1, 1, 0) } },
            null,
            [0, 0, 1, 1], [0, 0, 1, 1]);
        var n = patch.NormalAt(0.5, 0.5);
        Assert.Equal(1, Math.Abs(n.Dot(Vector3d.UnitZ)), 9);
    }
}
