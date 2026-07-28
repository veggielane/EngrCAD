using EngrCAD.Core;
using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// Simplicity validation for region loops. Before this existed a self-intersecting outer
/// loop produced garbage in silence: the constructor checked every loop against every OTHER
/// loop and never against itself.
/// </summary>
public class Region2dValidationTests
{
    /// <summary>Bow-tie with unequal lobes — the classic self-intersecting quad.</summary>
    private static Vector2d[] BowTie() =>
        [new(0, 0), new(4, 6), new(4, 0), new(0, 4)];

    /// <summary>A regular n-gon, always simple, at the given radius.</summary>
    private static Vector2d[] Polygon(int n, double radius = 10, double phase = 0)
    {
        var points = new Vector2d[n];
        for (int i = 0; i < n; i++)
        {
            double a = phase + 2 * Math.PI * i / n;
            points[i] = new Vector2d(radius * Math.Cos(a), radius * Math.Sin(a));
        }
        return points;
    }

    /// <summary>An n-gon with two non-adjacent vertices swapped: still n segments, but two
    /// of them now cross.</summary>
    private static Vector2d[] TangledPolygon(int n)
    {
        var points = Polygon(n);
        (points[1], points[n / 2]) = (points[n / 2], points[1]);
        return points;
    }

    [Fact]
    public void SelfIntersectingOuterLoop_IsRefusedByName()
    {
        var error = Assert.Throws<ArgumentException>(() => new Region2d(BowTie()));
        Assert.Contains("outer loop crosses itself", error.Message);
        // The two crossing segments are named, and the crossing point is on the diagonal
        // x = y between (0,0)-(4,6) and (4,0)-(0,4).
        Assert.Contains("segment 0", error.Message);
        Assert.Contains("segment 2", error.Message);
    }

    [Fact]
    public void SelfIntersectingHole_IsRefusedByName()
    {
        var outer = new Vector2d[] { new(-10, -10), new(10, -10), new(10, 10), new(-10, 10) };
        var error = Assert.Throws<ArgumentException>(() => new Region2d(outer, [BowTie()]));
        Assert.Contains("Hole loop 0 crosses itself", error.Message);
    }

    [Fact]
    public void EqualLobedBowTie_IsRefusedAsSelfIntersecting_NotAsZeroArea()
    {
        // The lobes cancel exactly, so the shoelace signed area is 0 and the old
        // enclosed-area guard would have blamed the wrong defect. Simplicity is therefore
        // checked FIRST.
        Vector2d[] symmetric = [new(0, 0), new(4, 4), new(4, 0), new(0, 4)];
        Assert.Equal(0, Region2d.SignedArea(symmetric));
        var error = Assert.Throws<ArgumentException>(() => new Region2d(symmetric));
        Assert.Contains("crosses itself", error.Message);
    }

    [Fact]
    public void FromLoops_RefusesASelfIntersectingLoop_RatherThanDroppingIt()
    {
        // FromLoops filters out zero-area loops, so the symmetric bow-tie used to vanish
        // without a word — the worst possible failure mode for a sketch front door.
        Vector2d[] symmetric = [new(0, 0), new(4, 4), new(4, 0), new(0, 4)];
        var error = Assert.Throws<ArgumentException>(
            () => Region2d.FromLoops([symmetric]));
        Assert.Contains("Input loop 0 crosses itself", error.Message);
    }

    [Fact]
    public void FromLoops_ChecksOnlySelfCrossingsAtItsOwnDoor()
    {
        // The bag is unsorted, so two loops in it are not yet known to share a region and
        // must not be cross-checked here — only self-crossings are unambiguously garbage.
        // Disjoint loops therefore still sort into two regions...
        var a = new Vector2d[] { new(0, 0), new(4, 0), new(4, 4), new(0, 4) };
        var b = new Vector2d[] { new(10, 10), new(14, 10), new(14, 14), new(10, 14) };
        Assert.Equal(2, Region2d.FromLoops([a, b]).Count);

        // ...while loops that partly overlap are caught one step later, by the constructor,
        // when containment sorting has decided they belong to the same region.
        var overlapping = new Vector2d[] { new(2, 2), new(6, 2), new(6, 6), new(2, 6) };
        var error = Assert.Throws<ArgumentException>(() => Region2d.FromLoops([a, overlapping]));
        Assert.Contains("crosses", error.Message);
    }

    [Fact]
    public void LoopsTouchingAtAVertex_AreAccepted()
    {
        // The documented convention: touching is not crossing. A hole whose corner sits
        // exactly on the outer boundary stays legal, as it was before.
        var outer = new Vector2d[] { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var hole = new Vector2d[] { new(5, 0), new(8, 3), new(2, 3) };
        var region = new Region2d(outer, [hole]);
        Assert.Equal(100 - 9, region.Area, 12);
    }

    [Fact]
    public void CrossingHoleAndOuterLoop_IsStillRefused()
    {
        var outer = new Vector2d[] { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        var hole = new Vector2d[] { new(8, 5), new(14, 5), new(14, 8), new(8, 8) };
        var error = Assert.Throws<ArgumentException>(() => new Region2d(outer, [hole]));
        Assert.Contains("Hole loop 0", error.Message);
    }

    [Theory]
    [InlineData(6)]    // brute-force path
    [InlineData(8)]
    [InlineData(64)]   // BVH path
    [InlineData(400)]
    public void BothCandidatePaths_AgreeOnSimpleAndTangledPolygons(int n)
    {
        // Below Region2dValidation.BruteForceLimit segments the all-pairs scan runs; above
        // it a BVH supplies the candidate pairs. The two must not disagree about what a
        // crossing is.
        Assert.False(Region2dValidation.TryFindSelfIntersection(Polygon(n), out _));
        Assert.True(Region2dValidation.TryFindSelfIntersection(TangledPolygon(n), out var crossing));
        Assert.True(crossing.IsSelfIntersection);
    }

    [Fact]
    public void ADenseSimpleLoop_ValidatesAndIsAccepted()
    {
        // 4 000 segments: the BVH keeps this linear in practice, where an all-pairs scan
        // would be 8 million predicate calls.
        var circle = Polygon(4000, radius: 25);
        var region = new Region2d(circle);
        Assert.Equal(Math.PI * 625, region.Area, 0);
    }

    [Fact]
    public void ReportedCrossingPoint_LiesOnBothSegments()
    {
        Assert.True(Region2dValidation.TryFindSelfIntersection(BowTie(), out var crossing));
        var loop = BowTie();
        var (a, b) = (loop[crossing.FirstSegment], loop[(crossing.FirstSegment + 1) % loop.Length]);
        var (c, d) = (loop[crossing.SecondSegment], loop[(crossing.SecondSegment + 1) % loop.Length]);
        Assert.Equal(0, (b - a).Cross(crossing.Point - a), 9);
        Assert.Equal(0, (d - c).Cross(crossing.Point - c), 9);
    }
}
