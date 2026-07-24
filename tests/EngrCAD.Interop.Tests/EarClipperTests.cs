using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// Direct unit tests for <see cref="TrimmedFaceTessellator"/>'s exact-coordinate ear
/// clipper (via InternalsVisibleTo). These pin the properties the trimmed-face path
/// depends on for future refactors: every input vertex survives (dropping an exactly
/// uv-collinear sample opens an unzippable crack on a curved surface), no zero-area
/// triangles are emitted, diagonals never pass through a vertex, and hole bridging
/// treats nearly-collinear contact as blocking.
/// </summary>
public class EarClipperTests
{
    private static double RingSignedArea(List<Vector2d> uv, List<int> ring)
    {
        double area = 0;
        for (int i = 0; i < ring.Count; i++)
            area += uv[ring[i]].Cross(uv[ring[(i + 1) % ring.Count]]);
        return area / 2;
    }

    /// <summary>
    /// Runs the clipper and verifies the invariants: success, all triangles strictly
    /// CCW, every ring vertex used, and the triangle areas summing exactly to the
    /// region area (outer minus holes).
    /// </summary>
    private static List<(int A, int B, int C)> ClipAndVerify(List<Vector2d> uv, List<List<int>> rings)
    {
        var triangles = TrimmedFaceTessellator.EarClip(uv, rings);
        Assert.NotNull(triangles);

        double covered = 0;
        foreach (var (a, b, c) in triangles!)
        {
            double doubleArea = (uv[b] - uv[a]).Cross(uv[c] - uv[a]);
            Assert.True(doubleArea > 0, $"non-CCW or zero-area triangle ({a},{b},{c})");
            covered += doubleArea / 2;
        }

        var used = new HashSet<int>();
        foreach (var (a, b, c) in triangles)
        {
            used.Add(a);
            used.Add(b);
            used.Add(c);
        }
        foreach (var ring in rings)
        {
            foreach (int i in ring)
                Assert.Contains(i, used);
        }

        double expected = Math.Abs(RingSignedArea(uv, rings[0]))
            - rings.Skip(1).Sum(r => Math.Abs(RingSignedArea(uv, r)));
        double scale = Math.Max(1, Math.Abs(expected));
        Assert.True(Math.Abs(covered - expected) <= 1e-9 * scale,
            $"triangles cover {covered}, region area is {expected}");
        return triangles;
    }

    private static (List<Vector2d> Uv, List<List<int>> Rings) Polygon(params Vector2d[][] rings)
    {
        var uv = new List<Vector2d>();
        var indexRings = new List<List<int>>();
        foreach (var ring in rings)
        {
            var indices = new List<int>(ring.Length);
            foreach (var p in ring)
            {
                indices.Add(uv.Count);
                uv.Add(p);
            }
            indexRings.Add(indices);
        }
        return (uv, indexRings);
    }

    [Fact]
    public void Comb_DeepAlternatingTeeth_TriangulateCompletely()
    {
        // Six deep teeth: reflex notches force the clipper to work around many blocked
        // ears instead of fanning from a corner.
        var outer = new List<Vector2d> { (0, 0), (12, 0), (12, 3) };
        for (int x = 11; x >= 1; x -= 2)
        {
            outer.Add(new Vector2d(x, 0.5));
            outer.Add(new Vector2d(x - 1, 3));
        }
        var (uv, rings) = Polygon([.. outer]);
        var triangles = ClipAndVerify(uv, rings);
        Assert.Equal(outer.Count - 2, triangles.Count);
    }

    [Fact]
    public void RectangularSpiral_ManyReflexCorners_TriangulatesCompletely()
    {
        var (uv, rings) = Polygon(
            [(0, 0), (10, 0), (10, 10), (2, 10), (2, 4), (4, 4), (4, 8), (8, 8), (8, 2), (0, 2)]);
        var triangles = ClipAndVerify(uv, rings);
        Assert.Equal(8, triangles.Count);
    }

    [Fact]
    public void TwoHoles_BridgeLandsNearTheOtherHole_BothSplice()
    {
        // The second hole's closest bridge candidates point at the first hole's
        // vertices (already spliced into the outer ring): bridging must stay valid with
        // another hole 0.5 units away.
        var (uv, rings) = Polygon(
            [(0, 0), (12, 0), (12, 10), (0, 10)],
            [(3, 4), (3, 6), (5, 6), (5, 4)],
            [(5.5, 4), (5.5, 6), (7.5, 6), (7.5, 4)]);
        ClipAndVerify(uv, rings);
    }

    [Fact]
    public void VertexExactlyOnCandidateDiagonal_BlocksTheEar()
    {
        // The ear at (6,0) has diagonal (0,0)-(6,6) passing exactly through the reflex
        // vertex (3,3): the blocking band must reject it, and the final triangulation
        // must still cover the polygon with (3,3) kept.
        var (uv, rings) = Polygon([(0, 0), (6, 0), (6, 6), (3, 3), (0, 6)]);
        var triangles = ClipAndVerify(uv, rings);
        Assert.Equal(3, triangles.Count);

        // No triangle may contain the reflex vertex strictly inside (a diagonal through
        // it would have produced exactly that).
        var reflex = new Vector2d(3, 3);
        foreach (var (a, b, c) in triangles)
        {
            bool strictlyInside =
                (uv[b] - uv[a]).Cross(reflex - uv[a]) > 1e-12 &&
                (uv[c] - uv[b]).Cross(reflex - uv[b]) > 1e-12 &&
                (uv[a] - uv[c]).Cross(reflex - uv[c]) > 1e-12;
            Assert.False(strictlyInside, $"diagonal passed through the reflex vertex in ({a},{b},{c})");
        }
    }

    [Fact]
    public void CollinearIsoParameterRuns_EveryVertexSurvives()
    {
        // L-shaped region with exactly-collinear runs at several v levels (v = 0, 1, 3)
        // and along two u levels — the earcut-would-filter-these case. ClipAndVerify
        // asserts every vertex is used and no zero-area triangle appears.
        var (uv, rings) = Polygon(
        [
            (0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0),
            (6, 1), (5, 1), (4, 1), (3, 1),
            (3, 2), (3, 3), (2, 3), (1, 3), (0, 3),
            (0, 2), (0, 1),
        ]);
        ClipAndVerify(uv, rings);
    }

    [Fact]
    public void CollinearRunsAroundAHole_EveryVertexSurvives()
    {
        // A band-like rectangle with subdivided (exactly collinear) top and bottom runs
        // and a hole between them — the shape of an unrolled ring face with a drill hole.
        var outer = new List<Vector2d>();
        for (int i = 0; i <= 10; i++)
            outer.Add(new Vector2d(i, 0));
        for (int i = 10; i >= 0; i--)
            outer.Add(new Vector2d(i, 4));
        var (uv, rings) = Polygon(
            [.. outer],
            [(4.5, 1.5), (4.5, 2.5), (5.5, 2.5), (5.5, 1.5)]);
        ClipAndVerify(uv, rings);
    }

    // ---- SegmentsTouch tolerance (nearly-collinear contact) ----

    [Fact]
    public void SegmentsTouch_NearlyCollinearOverlap_IsDetected()
    {
        // An edge lying along the candidate bridge but one ulp off its line: exact-zero
        // cross products missed this (d = -2e-13, not 0), letting a bridge overlap an
        // edge and leaving the spliced polygon self-intersecting.
        var p = new Vector2d(0, 0);
        var q = new Vector2d(10, 1e-13);
        var a = new Vector2d(2, 0);
        var b = new Vector2d(8, 0);
        Assert.True(TrimmedFaceTessellator.SegmentsTouch(p, q, a, b, 1e-8));
    }

    [Fact]
    public void SegmentsTouch_NearMissEndpointContact_IsDetected()
    {
        // Segment endpoint a hair off the other segment's interior.
        var p = new Vector2d(0, 0);
        var q = new Vector2d(10, 0);
        var a = new Vector2d(5, 1e-12);
        var b = new Vector2d(5, 4);
        Assert.True(TrimmedFaceTessellator.SegmentsTouch(p, q, a, b, 1e-8));
    }

    [Fact]
    public void SegmentsTouch_ClearlySeparated_IsNot()
    {
        Assert.False(TrimmedFaceTessellator.SegmentsTouch(
            new Vector2d(0, 0), new Vector2d(10, 0),
            new Vector2d(2, 1), new Vector2d(8, 1), 1e-8));
        // Collinear but disjoint spans do not touch.
        Assert.False(TrimmedFaceTessellator.SegmentsTouch(
            new Vector2d(0, 0), new Vector2d(4, 0),
            new Vector2d(5, 0), new Vector2d(9, 0), 1e-8));
    }

    [Fact]
    public void SegmentsTouch_ProperCrossing_StillDetected()
    {
        Assert.True(TrimmedFaceTessellator.SegmentsTouch(
            new Vector2d(0, 0), new Vector2d(10, 10),
            new Vector2d(0, 10), new Vector2d(10, 0), 1e-8));
    }
}
