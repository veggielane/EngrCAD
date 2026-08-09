using EngrCAD.Core.Geometry2;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// The curved twin of <see cref="Region2dValidationTests"/>: a chain of lines and arcs is
/// simple, or it is refused by name. The broad phase is a <see cref="EngrCAD.Core.Spatial.Bvh"/>
/// above <see cref="Region2dValidation.BruteForceLimit"/> edges, and the tests below drive
/// BOTH sides of that threshold with the same geometry — a broad phase that changed a verdict
/// would be a bug, and a broad phase never exercised would be a claim.
/// </summary>
public class CurvedRegion2dValidationTests
{
    /// <summary>A bow-tie: two straight edges crossing in their interiors.</summary>
    private static CurvedEdge2d[] BowTie() =>
    [
        CurvedEdge2d.Line((0, 0), (10, 10)),
        CurvedEdge2d.Line((10, 10), (10, 0)),
        CurvedEdge2d.Line((10, 0), (0, 10)),
        CurvedEdge2d.Line((0, 10), (0, 0)),
    ];

    [Fact]
    public void ASelfCrossingChain_IsRefusedNamingTheEdges()
    {
        Assert.True(CurvedRegion2dValidation.TryFindSelfIntersection(BowTie(), out var crossing));
        Assert.True(crossing.IsSelfIntersection);
        Assert.Equal(0, crossing.FirstLoop);

        var error = Assert.Throws<ArgumentException>(() => new CurvedRegion2d(BowTie()));
        Assert.Contains("outer chain", error.Message);
        Assert.Contains("crosses itself", error.Message);
    }

    [Fact]
    public void AnArcCrossingItsOwnChain_IsRefused()
    {
        // A "P" whose bowl loops back through the stem.
        var chain = new[]
        {
            CurvedEdge2d.Line((0, 0), (0, 10)),
            CurvedEdge2d.Arc((2, 7), 3, Math.PI / 2, -Math.PI).WithEndpoints((2, 10), (2, 4)),
            CurvedEdge2d.Line((2, 4), (-2, 4)),
            CurvedEdge2d.Line((-2, 4), (0, 0)),
        };
        Assert.True(CurvedRegion2dValidation.TryFindSelfIntersection(chain, out _));
    }

    [Fact]
    public void TangentialContact_IsLegal()
    {
        // A disc sitting exactly on a straight run: for lines and arcs a tangency is always a
        // TOUCH, never a crossing, so it does not separate the boundary into interior and
        // exterior and must not be refused.
        var chain = new[]
        {
            CurvedEdge2d.Line((-10, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (10, 10)),
            CurvedEdge2d.Line((10, 10), (-10, 10)),
            CurvedEdge2d.Line((-10, 10), (-10, 0)),
        };
        var hole = new[] { CurvedEdge2d.Circle((0, 3), 3) };   // touches y = 0 from above
        Assert.False(CurvedRegion2dValidation.TryFindCrossing([chain, hole], out _));
        var region = new CurvedRegion2d(chain, [hole]);
        Assert.Equal(200 - 9 * Math.PI, region.Area, 9);
    }

    /// <summary>
    /// The broad phase must not change the answer. The same bow-tie is padded with harmless
    /// far-away edges until it crosses the tree threshold, and both readings must agree —
    /// with the padded one asserted to be on the OTHER side of the threshold, so the test
    /// cannot quietly stop exercising the tree.
    /// </summary>
    [Fact]
    public void TheBvhBroadPhaseFindsTheSameCrossingTheScanDoes()
    {
        var small = BowTie();
        Assert.True(small.Length <= Region2dValidation.BruteForceLimit);
        Assert.True(CurvedRegion2dValidation.TryFindSelfIntersection(small, out _));

        // A separate, simple square chain far away, finely subdivided so the total edge count
        // clears the threshold. It cannot itself cross anything.
        var padding = new List<CurvedEdge2d>();
        const int pieces = 12;
        Vector2d[] corners = [new(100, 100), new(140, 100), new(140, 140), new(100, 140)];
        for (int c = 0; c < 4; c++)
        {
            for (int i = 0; i < pieces; i++)
            {
                padding.Add(CurvedEdge2d.Line(
                    Vector2d.Lerp(corners[c], corners[(c + 1) % 4], (double)i / pieces),
                    Vector2d.Lerp(corners[c], corners[(c + 1) % 4], (double)(i + 1) / pieces)));
            }
        }
        Assert.True(small.Length + padding.Count > Region2dValidation.BruteForceLimit);

        Assert.True(CurvedRegion2dValidation.TryFindCrossing([small, padding], out var crossing));
        Assert.Equal(0, crossing.FirstLoop);
        Assert.True(crossing.IsSelfIntersection);

        // The padding alone is simple, so the tree reports no crossing for it.
        Assert.False(CurvedRegion2dValidation.TryFindCrossing([padding], out _));
    }

    [Fact]
    public void TwoChainsCrossingEachOther_AreRefusedAcrossLoopsOnly()
    {
        var outer = new[]
        {
            CurvedEdge2d.Line((0, 0), (10, 0)),
            CurvedEdge2d.Line((10, 0), (10, 10)),
            CurvedEdge2d.Line((10, 10), (0, 10)),
            CurvedEdge2d.Line((0, 10), (0, 0)),
        };
        var straddling = new[] { CurvedEdge2d.Circle((10, 5), 3) };   // crosses the right wall

        Assert.True(CurvedRegion2dValidation.TryFindCrossing([outer, straddling], out var crossing));
        Assert.False(crossing.IsSelfIntersection);
        // With acrossLoops cleared the pair is legal — an unsorted bag of loops may overlap.
        Assert.False(CurvedRegion2dValidation.TryFindCrossing(
            [outer, straddling], out _, acrossLoops: false));
    }
}
