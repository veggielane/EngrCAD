using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class FaceGeometryTests
{
    [Fact]
    public void TryProjectPoint_AnalyticSurfaces_RoundTrip()
    {
        var cylinder = new CylinderSurface((1, 2, 3), Vector3d.UnitX, Vector3d.UnitY, 1.5);
        var sphere = new SphereSurface((0, 0, 0), 2.0);
        var plane = new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY);

        var rng = new Random(41);
        for (int i = 0; i < 30; i++)
        {
            double u = rng.NextDouble() * 2 * Math.PI;
            double v = rng.NextDouble() * 4 - 2;

            var pc = cylinder.PointAt(u, v);
            Assert.True(cylinder.TryProjectPoint(pc, out var uvc));
            Assert.True(cylinder.PointAt(uvc.X, uvc.Y).AreEqual(pc, new Tolerance(1e-9, 1e-9)));

            double lat = (rng.NextDouble() - 0.5) * 3; // stay off the exact poles
            var ps = sphere.PointAt(u, lat / 2);
            Assert.True(sphere.TryProjectPoint(ps, out var uvs));
            Assert.True(sphere.PointAt(uvs.X, uvs.Y).AreEqual(ps, new Tolerance(1e-9, 1e-9)));

            var pp = plane.PointAt(v, u);
            Assert.True(plane.TryProjectPoint(pp, out var uvp));
            Assert.True(uvp.AreEqual(new Vector2d(v, u), new Tolerance(1e-9, 1e-9)));
        }

        // Off-surface points are rejected.
        Assert.False(cylinder.TryProjectPoint((1, 2, 3), out _));       // on the axis
        Assert.False(sphere.TryProjectPoint((3, 0, 0), out _));
        Assert.False(plane.TryProjectPoint((0, 0, 2), out _));
    }

    [Fact]
    public void TryProjectPoint_Newton_OnNurbsPatch()
    {
        var patch = new NurbsSurface(
            2, 2,
            new Vector3d[3, 3]
            {
                { (0, 0, 0), (0, 1, 0.5), (0, 2, 0) },
                { (1, 0, 0.5), (1, 1, 1.5), (1, 2, 0.5) },
                { (2, 0, 0), (2, 1, 0.5), (2, 2, 0) },
            },
            null,
            [0, 0, 0, 1, 1, 1], [0, 0, 0, 1, 1, 1]);

        var target = patch.PointAt(0.37, 0.62);
        Assert.True(patch.TryProjectPoint(target, out var uv));
        Assert.True(patch.PointAt(uv.X, uv.Y).AreEqual(target, new Tolerance(1e-7, 1e-7)));
    }

    [Fact]
    public void PullCurve_TiltedEllipseOntoCylinder_IsContinuousSinusoid()
    {
        var cylinder = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 1.0);
        double tilt = Math.PI / 6;
        var normal = new Vector3d(Math.Sin(tilt), 0, Math.Cos(tilt));
        var x = normal.ArbitraryPerpendicular(Tolerance.Default);
        var plane = new PlaneSurface((0, 0, 0), x, normal.Cross(x));
        var ellipse = Assert.Single(SurfaceIntersection.Intersect(plane, cylinder,
            new Aabb((-3, -3, -3), (3, 3, 3))));

        var pulled = FaceGeometry.PullCurve(ellipse, cylinder, samples: 96);

        // Continuous in u (no 2π jumps) and spanning one full period.
        for (int i = 1; i < pulled.Count; i++)
            Assert.True(Math.Abs(pulled[i].X - pulled[i - 1].X) < 0.5, $"u jump at {i}");
        double span = pulled.Max(p => p.X) - pulled.Min(p => p.X);
        Assert.True(Math.Abs(span - 2 * Math.PI) < 0.2, $"u span {span}");

        // v follows the plane: v = -tan(tilt) · cos(u-ish) — verify samples re-evaluate onto the curve.
        for (int i = 0; i < pulled.Count; i++)
        {
            var p = cylinder.PointAt(pulled[i].X, pulled[i].Y);
            Assert.True(Math.Abs((p - plane.Origin).Dot(plane.Normal)) < 1e-6, "pulled point off plane");
        }
    }

    [Fact]
    public void Contains_PlanarFaceAndCylinderBand()
    {
        var box = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 2, 1)));
        var top = box.Faces.First(f => f.Surface is PlaneSurface p && p.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));

        Assert.True(FaceGeometry.Contains(top, (1, 1, 1)));
        Assert.True(FaceGeometry.Contains(top, (0.1, 1.7, 1)));
        Assert.False(FaceGeometry.Contains(top, (2.5, 1, 1)));  // beyond the face
        Assert.False(FaceGeometry.Contains(top, (1, 1, 0.5)));  // not on the surface

        var cylinder = SolidFactory.MakeCylinder(1.0, 2.0);
        var band = cylinder.Faces.First(f => f.Surface is CylinderSurface);
        Assert.True(FaceGeometry.Contains(band, (1, 0, 1)));
        Assert.True(FaceGeometry.Contains(band, (0, -1, 0.2)));
        Assert.False(FaceGeometry.Contains(band, (1, 0, 2.5))); // beyond the top cap
        Assert.False(FaceGeometry.Contains(band, (0.5, 0, 1))); // interior, not on the surface
    }

    [Fact]
    public void SplitByClosedCurve_ProducesManifoldHoleAndDisk()
    {
        var box = SolidFactory.MakeBox(new Aabb((-1, -1, 0), (1, 1, 1)));
        var top = box.Faces.First(f => f.Surface is PlaneSurface p && p.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));
        var bore = new CylinderSurface((0, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 0.5);
        var circle = Assert.Single(SurfaceIntersection.Intersect(top.Surface, bore,
            new Aabb((-2, -2, -1), (2, 2, 2))));

        var split = FaceSplitter.SplitByClosedCurve(top, circle);

        Assert.Equal(2, split.FaceWithHole.Loops.Count);
        Assert.NotNull(split.Disk);
        Assert.Equal(2, split.Edge.Uses.Count);
        Assert.NotEqual(split.Edge.Uses[0].SameSense, split.Edge.Uses[1].SameSense);

        // Containment flips across the circle.
        Assert.True(FaceGeometry.Contains(split.FaceWithHole, (0.8, 0.8, 1)));
        Assert.False(FaceGeometry.Contains(split.FaceWithHole, (0, 0, 1)));
        Assert.True(FaceGeometry.Contains(split.Disk, (0, 0, 1)));
        Assert.False(FaceGeometry.Contains(split.Disk, (0.8, 0.8, 1)));

        // A curve outside the face is rejected.
        var farBore = new CylinderSurface((5, 0, 0), Vector3d.UnitX, Vector3d.UnitY, 0.5);
        var farCircle = Assert.Single(SurfaceIntersection.Intersect(
            new PlaneSurface((0, 0, 1), Vector3d.UnitX, Vector3d.UnitY), farBore,
            new Aabb((3, -2, -1), (7, 2, 2))));
        Assert.Throws<ArgumentException>(() => FaceSplitter.SplitByClosedCurve(split.FaceWithHole, farCircle));
    }
}
