using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

public class BrepSelectionTests
{
    private static BrepSolid Box(double x = 2, double y = 1, double z = 1) =>
        SolidFactory.MakeBox(new Aabb((0, 0, 0), (x, y, z)));

    // ---- sorting / extremes ----

    [Fact]
    public void SortAlong_OrdersBoxFacesByPlaneOffset()
    {
        var box = Box(2, 1, 3);
        var sorted = box.Faces.SortAlong(Vector3d.UnitZ);

        Assert.Equal(box.Faces.Count(), sorted.Count);
        // Bottom face (z = 0) first, top face (z = 3) last; the four side faces rank by
        // their bounds centre z = 1.5 in between.
        Assert.True(sorted[0].IsPlanar(out var bottomOrigin, out var bottomNormal));
        Assert.Equal(0, bottomOrigin.Z, 12);
        Assert.Equal(-1, bottomNormal.Z, 12);
        Assert.True(sorted[^1].IsPlanar(out var topOrigin, out var topNormal));
        Assert.Equal(3, topOrigin.Z, 12);
        Assert.Equal(1, topNormal.Z, 12);
    }

    [Fact]
    public void ExtremeHighestLowest_AgreeAndPickPlaneOffsets()
    {
        var box = Box(2, 1, 3);
        var top = box.Faces.Highest();
        var bottom = box.Faces.Lowest();

        Assert.Same(top, box.Faces.Extreme(Vector3d.UnitZ));
        Assert.Same(bottom, box.Faces.Extreme(-Vector3d.UnitZ));
        Assert.True(top.IsPlanar(out var origin, out _));
        Assert.Equal(3, origin.Z, 12);
        Assert.NotSame(top, bottom);

        var rightmost = box.Faces.Extreme(Vector3d.UnitX);
        Assert.True(rightmost.IsPlanar(out var xOrigin, out var xNormal));
        Assert.Equal(2, xOrigin.X, 12);
        Assert.Equal(1, xNormal.X, 12);
    }

    [Fact]
    public void Extreme_EmptySequence_ThrowsLoudly()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Array.Empty<BrepFace>().Extreme(Vector3d.UnitZ));
        Assert.Contains("empty", exception.Message);
    }

    [Fact]
    public void EdgeSortAndExtreme_UseCurveMidpoints()
    {
        var box = Box(2, 1, 3);
        var edges = box.Edges.ToList();
        Assert.Equal(12, edges.Count);

        // 4 edges at z = 0, 4 vertical (midpoint z = 1.5), 4 at z = 3.
        var sorted = edges.SortAlong(Vector3d.UnitZ);
        Assert.Equal(0, sorted[0].RankAlong(Vector3d.UnitZ), 12);
        Assert.Equal(3, sorted[^1].RankAlong(Vector3d.UnitZ), 12);
        Assert.Equal(3, edges.Highest().RankAlong(Vector3d.UnitZ), 12);
        Assert.Equal(0, edges.Lowest().RankAlong(Vector3d.UnitZ), 12);
    }

    // ---- grouping ----

    [Fact]
    public void GroupAlong_BoxFacesFormThreeLevels()
    {
        var box = Box(2, 1, 3);
        var groups = box.Faces.GroupAlong(Vector3d.UnitZ);

        Assert.Equal(3, groups.Count);
        Assert.Single(groups[0]);      // bottom (z = 0)
        Assert.Equal(4, groups[1].Count); // side faces, bounds centre z = 1.5
        Assert.Single(groups[2]);      // top (z = 3)
        Assert.True(groups[2][0].IsPlanar(out var origin, out _));
        Assert.Equal(3, origin.Z, 12);
    }

    [Fact]
    public void GroupAlong_EdgesOfBox_ThreeLevels()
    {
        var box = Box(2, 1, 3);
        var groups = box.Edges.GroupAlong(Vector3d.UnitZ);
        Assert.Equal(3, groups.Count);
        Assert.Equal(4, groups[0].Count);
        Assert.Equal(4, groups[1].Count);
        Assert.Equal(4, groups[2].Count);
    }

    [Fact]
    public void GroupByCoplanar_LProfileExtrusion_SeparatesParallelOffsetFaces()
    {
        // An L-shaped prism: 6 side faces + top + bottom = 8 faces. The two +X-facing
        // side walls sit at different x offsets, so they must land in different groups
        // even though their normals agree.
        var profile = Profile.FromPoints([
            (0, 0, 0), (2, 0, 0), (2, 1, 0), (1, 1, 0), (1, 2, 0), (0, 2, 0)]);
        var lPrism = SolidFactory.Extrude(profile, (0, 0, 1));

        var groups = lPrism.Faces.GroupByCoplanar();
        Assert.Equal(lPrism.Faces.Count(), groups.Sum(g => g.Count));
        // No two faces of a convex-cornered L-prism are coplanar, so every group is a
        // singleton — the point is that parallel-but-offset faces do NOT merge.
        Assert.All(groups, g => Assert.Single(g));
    }

    [Fact]
    public void GroupByCoplanar_MergesGenuinelyCoplanarFaces()
    {
        // Two boxes listed together: side-by-side at the same height, so top faces are
        // coplanar (z = 1) and merge across the solids, while the boxes' facing walls
        // (x = 2 outward vs x = 3 outward-opposite) stay apart.
        var a = SolidFactory.MakeBox(new Aabb((0, 0, 0), (2, 1, 1)));
        var b = SolidFactory.MakeBox(new Aabb((3, 0, 0), (5, 1, 1)));
        var faces = a.Faces.Concat(b.Faces).ToList();

        var groups = faces.GroupByCoplanar();
        var tops = groups.Single(g =>
            g[0].IsPlanar(out var o, out var n) && n.Z > 0.5 && Math.Abs(o.Z - 1) < 1e-9);
        Assert.Equal(2, tops.Count);
    }

    // ---- filtering ----

    [Fact]
    public void FilterBy_CylinderClassifiesSideAndCaps()
    {
        var cylinder = SolidFactory.MakeCylinder(3, 5);
        Assert.Single(cylinder.Faces.FilterBy(SurfaceKind.Cylindrical));
        Assert.Equal(2, cylinder.Faces.FilterBy(SurfaceKind.Planar).Count());
        Assert.Empty(cylinder.Faces.FilterBy(SurfaceKind.Spherical));
    }

    [Fact]
    public void Kind_ConeAndSphere()
    {
        var cone = SolidFactory.MakeCone(3, 1, 4);
        Assert.Contains(cone.Faces, f => f.Kind() == SurfaceKind.Conical);

        var sphere = SolidFactory.MakeSphere(2);
        Assert.Contains(sphere.Faces, f => f.Kind() == SurfaceKind.Spherical);
    }

    [Fact]
    public void Kind_TorusBandsAreToroidal()
    {
        // The torus factory's generators are rational NURBS arcs, so this locks the
        // sampling-based classification (a curve-type switch would call them Revolved).
        var torus = SolidFactory.MakeTorus(5, 1);
        Assert.All(torus.Faces, f => Assert.Equal(SurfaceKind.Toroidal, f.Kind()));
    }

    // ---- radius queries ----

    [Fact]
    public void NthByRadius_GroupsAcrossSolidsAscending()
    {
        var small = SolidFactory.MakeCylinder(1, 5);
        var alsoSmall = SolidFactory.MakeCylinder(1, 2);
        var large = SolidFactory.MakeCylinder(4, 5);
        var faces = small.Faces.Concat(alsoSmall.Faces).Concat(large.Faces).ToList();

        var groups = faces.GroupByRadius();
        Assert.Equal(2, groups.Count);
        Assert.Equal(1, groups[0].Radius, 12);
        Assert.Equal(2, groups[0].Faces.Count);
        Assert.Equal(4, groups[1].Radius, 12);

        Assert.Equal(2, faces.NthByRadius(0).Count);
        Assert.Single(faces.NthByRadius(1));
        Assert.Single(faces.NthByRadius(-1)); // negative counts from the largest
        Assert.Same(faces.NthByRadius(1)[0], faces.NthByRadius(-1)[0]);

        var exception = Assert.Throws<InvalidOperationException>(() => faces.NthByRadius(2));
        Assert.Contains("2 distinct", exception.Message);
        Assert.Contains("1", exception.Message);
    }

    [Fact]
    public void NthByRadius_CircularEdges()
    {
        var cylinder = SolidFactory.MakeCylinder(3, 5);
        var rims = cylinder.Edges.NthByRadius(0);
        Assert.NotEmpty(rims);
        Assert.All(rims, e =>
        {
            Assert.True(e.IsCircular(out _, out _, out double r));
            Assert.Equal(3, r, 12);
        });
    }

    // ---- area ----

    [Fact]
    public void Area_BoxFacesExact()
    {
        var box = Box(2, 1, 3);
        var top = box.Faces.Highest();
        Assert.Equal(2 * 1, top.Area(), 12);

        double total = box.Faces.Sum(f => f.Area());
        Assert.Equal(2 * (2 * 1 + 2 * 3 + 1 * 3), total, 12);
    }

    [Fact]
    public void Area_CircularCapExact()
    {
        var cylinder = SolidFactory.MakeCylinder(3, 5);
        var cap = cylinder.Faces.Highest();
        Assert.True(cap.IsPlanar(out _, out _));
        // The cap's rim is a full-circle edge: the closed-form arc term must give πr²
        // exactly (no sampling).
        Assert.Equal(Math.PI * 9, cap.Area(), 9);
    }

    [Fact]
    public void Area_CylinderBandWithinQuadratureTolerance()
    {
        var cylinder = SolidFactory.MakeCylinder(3, 5);
        var band = cylinder.Faces.FilterBy(SurfaceKind.Cylindrical).Single();
        double exact = 2 * Math.PI * 3 * 5;
        // Quadrature-grade: documented ~1-2%.
        Assert.InRange(band.Area(), exact * 0.97, exact * 1.03);
    }

    [Fact]
    public void LargestByArea_PicksThePlateFace()
    {
        var plate = Box(20, 30, 4);
        var largest = plate.Faces.LargestByArea();
        Assert.True(largest.IsPlanar(out _, out var normal));
        // Top/bottom (20×30 = 600) beat the sides (≤ 120).
        Assert.Equal(1, Math.Abs(normal.Z), 12);
        Assert.Equal(600, largest.Area(), 9);

        var sorted = plate.Faces.SortByArea();
        Assert.Equal(600, sorted[^1].Area(), 9);
        Assert.True(sorted[0].Area() <= sorted[^1].Area());
    }
}
