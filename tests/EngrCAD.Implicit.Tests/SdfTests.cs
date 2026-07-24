using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Implicit.Tests;

public class SdfTests
{
    private const double Precision = 1e-12;

    [Fact]
    public void Sphere_ExactDistances()
    {
        var sphere = Sdf.Sphere(2);
        Assert.Equal(-2, sphere.Evaluate((0, 0, 0)), Precision);
        Assert.Equal(0, sphere.Evaluate((2, 0, 0)), Precision);
        Assert.Equal(1, sphere.Evaluate((0, 3, 0)), Precision);
    }

    [Fact]
    public void Box_ExactDistances()
    {
        var box = Sdf.Box(2, 2, 2); // half extents 1

        Assert.Equal(-1, box.Evaluate((0, 0, 0)), Precision);          // center
        Assert.Equal(0, box.Evaluate((1, 0, 0)), Precision);           // face
        Assert.Equal(1, box.Evaluate((2, 0, 0)), Precision);           // beyond a face
        Assert.Equal(Math.Sqrt(3), box.Evaluate((2, 2, 2)), Precision); // beyond a corner
        Assert.Equal(Math.Sqrt(2), box.Evaluate((2, 2, 0)), Precision); // beyond an edge
        Assert.Equal(-0.25, box.Evaluate((0.75, 0, 0)), Precision);    // inside, nearest face
    }

    [Fact]
    public void Cylinder_DistancesOnAxesAndCaps()
    {
        var cylinder = Sdf.Cylinder(radius: 1, height: 4); // z in [-2, 2]
        Assert.Equal(-1, cylinder.Evaluate((0, 0, 0)), Precision);
        Assert.Equal(0, cylinder.Evaluate((1, 0, 0)), Precision);
        Assert.Equal(0, cylinder.Evaluate((0, 0, 2)), Precision);
        Assert.Equal(1, cylinder.Evaluate((2, 0, 0)), Precision);
        Assert.Equal(3, cylinder.Evaluate((0, 0, 5)), Precision);
        Assert.Equal(Math.Sqrt(2), cylinder.Evaluate((2, 0, 3)), Precision); // off rim
    }

    [Fact]
    public void Torus_DistancesInPlaneAndAbove()
    {
        var torus = Sdf.Torus(majorRadius: 2, minorRadius: 0.5);
        Assert.Equal(-0.5, torus.Evaluate((2, 0, 0)), Precision);  // ring center line
        Assert.Equal(0, torus.Evaluate((2.5, 0, 0)), Precision);
        Assert.Equal(0, torus.Evaluate((2, 0, 0.5)), Precision);
        Assert.Equal(1.5, torus.Evaluate((0, 0, 0)), Precision);   // hole center
    }

    [Fact]
    public void Capsule_EndSpheresAndShaft()
    {
        var capsule = Sdf.Capsule((0, 0, -1), (0, 0, 1), 0.5);
        Assert.Equal(-0.5, capsule.Evaluate((0, 0, 0)), Precision);
        Assert.Equal(0, capsule.Evaluate((0.5, 0, 0)), Precision);
        Assert.Equal(0.5, capsule.Evaluate((0, 0, 2)), Precision); // beyond an end cap
    }

    [Fact]
    public void SetOperators_MinMaxSemantics()
    {
        var a = Sdf.Sphere(1);
        var b = Sdf.Sphere(1).Translate((1.5, 0, 0));
        var p = new Vector3d(0.75, 0, 0);

        Assert.Equal(Math.Min(a.Evaluate(p), b.Evaluate(p)), (a | b).Evaluate(p), Precision);
        Assert.Equal(Math.Max(a.Evaluate(p), b.Evaluate(p)), (a & b).Evaluate(p), Precision);
        Assert.Equal(Math.Max(a.Evaluate(p), -b.Evaluate(p)), (a - b).Evaluate(p), Precision);
    }

    [Fact]
    public void SmoothUnion_LowerBoundOfMin_AndExactFarAway()
    {
        var a = Sdf.Sphere(1);
        var b = Sdf.Sphere(1).Translate((3, 0, 0));
        var smooth = a.SmoothUnion(b, 0.5);

        // In the blend region: at most min (fills the crease).
        var seam = new Vector3d(1.5, 0.8, 0);
        Assert.True(smooth.Evaluate(seam) <= Math.Min(a.Evaluate(seam), b.Evaluate(seam)) + Precision);

        // Far from the seam: exactly min.
        var far = new Vector3d(-2, 0, 0);
        Assert.Equal(Math.Min(a.Evaluate(far), b.Evaluate(far)), smooth.Evaluate(far), Precision);
    }

    [Fact]
    public void Transforms_MoveTheField()
    {
        var moved = Sdf.Sphere(1).Translate((5, 0, 0));
        Assert.Equal(-1, moved.Evaluate((5, 0, 0)), Precision);
        Assert.Equal(0, moved.Evaluate((6, 0, 0)), Precision);

        // Rotate a box 90° about Z: its long X side now lies along Y.
        var rotated = Sdf.Box(4, 2, 2).Rotate(Quaterniond.FromAxisAngle(Vector3d.UnitZ, Math.PI / 2));
        Assert.Equal(0, rotated.Evaluate((0, 2, 0)), 1e-9);
        Assert.Equal(1, rotated.Evaluate((2, 0, 0)), 1e-9);

        var scaled = Sdf.Sphere(1).Scale(2);
        Assert.Equal(-2, scaled.Evaluate((0, 0, 0)), Precision);
        Assert.Equal(2, scaled.Evaluate((4, 0, 0)), Precision);
    }

    [Fact]
    public void OffsetAndShell()
    {
        var grown = Sdf.Sphere(1).Offset(0.5);
        Assert.Equal(0, grown.Evaluate((1.5, 0, 0)), Precision);

        var shell = Sdf.Sphere(1).Shell(0.2);
        Assert.Equal(-0.1, shell.Evaluate((1, 0, 0)), Precision);   // mid-wall
        Assert.Equal(0, shell.Evaluate((1.1, 0, 0)), Precision);    // outer skin
        Assert.Equal(0, shell.Evaluate((0.9, 0, 0)), Precision);    // inner skin
        Assert.True(shell.Evaluate((0, 0, 0)) > 0);                 // hollow center
    }

    [Fact]
    public void Normal_IsRadialForSphere()
    {
        var sphere = Sdf.Sphere(1);
        var p = new Vector3d(0.6, -0.8, 0);
        var n = sphere.Normal(p);
        Assert.True(n.Dot(p.Normalized()) > 0.9999);
    }

    [Fact]
    public void Bounds_TrackComposition()
    {
        var a = Sdf.Sphere(1);
        var b = Sdf.Sphere(1).Translate((5, 0, 0));
        var union = a | b;

        Assert.True(union.Bounds.Contains(a.Bounds));
        Assert.True(union.Bounds.Contains(b.Bounds));
        Assert.Equal(6.0, union.Bounds.Max.X, Precision);

        // Difference keeps A's bounds; intersection shrinks to the overlap (empty here).
        Assert.Equal(a.Bounds, (a - b).Bounds);
        Assert.True((a & b).Bounds.IsEmpty);
    }

    [Fact]
    public void SmoothOperators_NegativeBlend_DegradesToHardOperator_WithoutShrinkingBounds()
    {
        var a = Sdf.Sphere(1);
        var b = Sdf.Sphere(1).Translate((1.5, 0, 0));
        const double k = -0.5;

        // Policy (binary + n-ary): blend <= 0 degrades to the exact hard operator — the
        // field already did, and the bounds expansion clamps at 0. Before the fix a
        // negative blend silently *shrank* the conservative bounds by |k|.
        Assert.Equal((a | b).Bounds, a.SmoothUnion(b, k).Bounds);
        Assert.Equal((a & b).Bounds, a.SmoothIntersect(b, k).Bounds);
        Assert.Equal((a - b).Bounds, a.SmoothSubtract(b, k).Bounds);
        Assert.Equal(Sdf.Union(a, b).Bounds, Sdf.SmoothUnion([a, b], k).Bounds);

        // Bounds stay conservative: every inside point of the degraded field is contained.
        Sdf[] degraded = [a.SmoothUnion(b, k), a.SmoothIntersect(b, k), a.SmoothSubtract(b, k)];
        Sdf[] hard = [a | b, a & b, a - b];
        for (int i = 0; i < degraded.Length; i++)
            for (int x = -4; x <= 6; x++)
                for (int y = -4; y <= 4; y++)
                {
                    var p = new Vector3d(0.4 * x, 0.4 * y, 0.1);
                    Assert.Equal(hard[i].Evaluate(p), degraded[i].Evaluate(p), Precision);
                    if (degraded[i].Evaluate(p) < 0)
                        Assert.True(degraded[i].Bounds.Contains(p));
                }
    }

    [Fact]
    public void UnboundedFields_ReportInfiniteBounds()
    {
        Assert.False(Sdf.IsFinite(Sdf.HalfSpace(Vector3d.UnitZ, 0).Bounds));
        Assert.False(Sdf.IsFinite(Sdf.Gyroid(1, 0.1).Bounds));

        // Intersecting with a finite solid restores finite bounds.
        var lattice = Sdf.Sphere(1) & Sdf.Gyroid(0.5, 0.1);
        Assert.True(Sdf.IsFinite(lattice.Bounds));
    }

    [Fact]
    public void BatchEvaluate_MatchesScalar()
    {
        var sdf = Sdf.Sphere(1).SmoothUnion(Sdf.Box(1, 1, 1).Translate((1, 0, 0)), 0.3);
        var rng = new Random(5);
        var points = new Vector3d[100];
        for (int i = 0; i < points.Length; i++)
            points[i] = (rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);

        var batch = new double[points.Length];
        sdf.Evaluate(points, batch);
        for (int i = 0; i < points.Length; i++)
            Assert.Equal(sdf.Evaluate(points[i]), batch[i], Precision);
    }
}
