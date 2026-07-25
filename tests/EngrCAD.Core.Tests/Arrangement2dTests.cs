using EngrCAD.Core;
using Xunit;
using EngrCAD.Core.Geometry2;

namespace EngrCAD.Core.Tests;

public class Arrangement2dTests
{
    private static double SignedArea(IReadOnlyList<Vector2d> loop)
    {
        double sum = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            sum += p.Cross(q);
        }
        return 0.5 * sum;
    }

    private static void InsertBox(Arrangement2d arrangement, Vector2d min, Vector2d max)
    {
        arrangement.InsertPolyline(
            [min, new Vector2d(max.X, min.Y), max, new Vector2d(min.X, max.Y)], closed: true);
    }

    [Fact]
    public void CrossingSegmentsInBox_ProduceFourCells()
    {
        var arrangement = new Arrangement2d();
        InsertBox(arrangement, (0, 0), (2, 2));
        arrangement.Insert((0, 1), (2, 1));
        arrangement.Insert((1, 0), (1, 2));

        // 4 corners + 4 side midpoints + centre; each side split in two + 4 cross arms.
        Assert.Equal(9, arrangement.VertexCount);
        Assert.Equal(12, arrangement.EdgeCount);

        var cells = arrangement.ExtractCells();
        Assert.Equal(4, cells.Count);
        Assert.All(cells, cell => Assert.Equal(1.0, cell.Area, 12));
        Assert.All(cells, cell => Assert.True(SignedArea(cell.Outer) > 0, "outer loops must be CCW"));
        Assert.Equal(4.0, cells.Sum(c => c.Area), 12);
    }

    [Fact]
    public void GridOfSegments_ProducesKnownCellCount()
    {
        var arrangement = new Arrangement2d();
        InsertBox(arrangement, (0, 0), (4, 3));
        for (int x = 1; x <= 3; x++)
            arrangement.Insert((x, 0), (x, 3));
        for (int y = 1; y <= 2; y++)
            arrangement.Insert((0, y), (4, y));

        var cells = arrangement.ExtractCells();
        Assert.Equal(12, cells.Count);
        Assert.All(cells, cell => Assert.Equal(1.0, cell.Area, 12));
        Assert.All(cells, cell => Assert.True(SignedArea(cell.Outer) > 0));
        Assert.Equal(12.0, cells.Sum(c => c.Area), 12);
    }

    [Fact]
    public void TJunctions_SplitTheEdgesTheyLandOn()
    {
        var arrangement = new Arrangement2d();
        InsertBox(arrangement, (0, 0), (2, 2));
        arrangement.Insert((1, 0), (1, 2)); // both endpoints interior to box edges

        Assert.Equal(6, arrangement.VertexCount); // 4 corners + 2 T-junction vertices
        Assert.Equal(7, arrangement.EdgeCount);   // top/bottom split + 2 sides + divider

        var cells = arrangement.ExtractCells();
        Assert.Equal(2, cells.Count);
        Assert.All(cells, cell => Assert.Equal(2.0, cell.Area, 12));
    }

    [Fact]
    public void SharedEndpointsAndDuplicateSegments_Dedupe()
    {
        var arrangement = new Arrangement2d();
        arrangement.InsertPolyline([(0, 0), (2, 0), (1, 1)], closed: true);
        arrangement.InsertPolyline([(0, 0), (2, 0), (1, -1)], closed: true);
        arrangement.Insert((0, 0), (2, 0)); // exact duplicate of the shared edge

        Assert.Equal(4, arrangement.VertexCount);
        Assert.Equal(5, arrangement.EdgeCount);

        var cells = arrangement.ExtractCells();
        Assert.Equal(2, cells.Count);
        Assert.All(cells, cell => Assert.Equal(1.0, cell.Area, 12));
    }

    [Fact]
    public void DanglingSegment_BecomesASlitWithoutChangingArea()
    {
        var arrangement = new Arrangement2d();
        InsertBox(arrangement, (0, 0), (2, 2));
        arrangement.Insert((1, 0), (1, 1)); // T on the bottom edge, dangling inward

        var cells = arrangement.ExtractCells();
        var cell = Assert.Single(cells);
        Assert.Equal(4.0, cell.Area, 12); // spur doubles back, exactly cancelling
        Assert.Empty(cell.Holes);
    }

    [Fact]
    public void IslandComponent_BecomesAHoleOfTheSurroundingCell()
    {
        var arrangement = new Arrangement2d();
        InsertBox(arrangement, (0, 0), (4, 4));
        InsertBox(arrangement, (1, 1), (2, 2)); // disconnected island

        var cells = arrangement.ExtractCells().OrderBy(c => c.Area).ToList();
        Assert.Equal(2, cells.Count);

        var inner = cells[0];
        Assert.Equal(1.0, inner.Area, 12);
        Assert.Empty(inner.Holes);

        var outer = cells[1];
        Assert.Equal(15.0, outer.Area, 12); // 16 minus the island
        var hole = Assert.Single(outer.Holes);
        Assert.True(SignedArea(hole) < 0, "hole loops must be CW");

        Assert.Equal(16.0, cells.Sum(c => c.Area), 12);
    }

    [Fact]
    public void CollinearOverlap_SharesTheCommonSubSegment()
    {
        var arrangement = new Arrangement2d();
        arrangement.Insert((0, 0), (4, 0));
        arrangement.Insert((1, 0), (6, 0)); // overlaps [1,4] exactly

        // Vertices 0,1,4,6; edges [0,1][1,4][4,6] — the overlap deduped into one edge.
        Assert.Equal(4, arrangement.VertexCount);
        Assert.Equal(3, arrangement.EdgeCount);
    }

    [Fact]
    public void NearDegenerateCrossings_AreDecidedByTheExactPredicates()
    {
        // The point of exact predicates: a segment endpoint sits within a few ulps of a
        // long diagonal, and whether the segment CROSSES the diagonal must follow the
        // exact side of that endpoint — where the naive determinant gets the side wrong.
        var diagA = new Vector2d(-2, -2);
        var diagB = new Vector2d(26, 26);
        double ulp = Math.BitIncrement(0.5) - 0.5;

        // Sample the Kettner grid, preferring points where the naive sign fails.
        var samples = new List<Vector2d>();
        var naiveWrong = new List<Vector2d>();
        for (int i = 0; i < 64 && naiveWrong.Count < 8; i++)
        {
            for (int j = 0; j < 64 && naiveWrong.Count < 8; j++)
            {
                var p = new Vector2d(0.5 + i * ulp, 0.5 + j * ulp);
                int exact = ExactReference.Orient2dSign(p, diagA, diagB);
                if (exact == 0)
                    continue;
                int naive = Math.Sign((p.X - diagB.X) * (diagA.Y - diagB.Y) - (p.Y - diagB.Y) * (diagA.X - diagB.X));
                if (naive != exact)
                    naiveWrong.Add(p);
                else if (samples.Count < 8)
                    samples.Add(p);
            }
        }
        Assert.True(naiveWrong.Count > 0, "expected naive-misclassified sample points");

        foreach (var p in samples.Concat(naiveWrong))
        {
            // A tiny snap tolerance so ulp-scale sidedness is topology, not noise.
            var arrangement = new Arrangement2d(vertexSnapTolerance: 1e-30);
            InsertBox(arrangement, (-2, -2), (26, 26));
            arrangement.Insert(diagA, diagB); // box diagonal, corner to corner
            Assert.Equal(4, arrangement.VertexCount);
            Assert.Equal(5, arrangement.EdgeCount);

            var q = new Vector2d(1.0, 20.0); // strictly above the diagonal
            arrangement.Insert(p, q);

            int side = ExactReference.Orient2dSign(p, diagA, diagB);
            if (side > 0)
            {
                // p above the diagonal: p-q stays inside the upper cell — no split.
                Assert.Equal(6, arrangement.VertexCount);
                Assert.Equal(6, arrangement.EdgeCount);
            }
            else
            {
                // p below: p-q crosses the diagonal — diagonal split, segment split.
                Assert.Equal(7, arrangement.VertexCount);
                Assert.Equal(8, arrangement.EdgeCount);
            }

            // Either way the spur does not change the cell decomposition.
            var cells = arrangement.ExtractCells();
            Assert.Equal(2, cells.Count);
            Assert.Equal(28.0 * 28.0, cells.Sum(c => c.Area), 6);
        }
    }

    [Fact]
    public void SegmentEndingExactlyOnHostileMagnitudeEdge_SplitsIt()
    {
        // T-junction whose on-edge decision needs the exact predicate: the junction point
        // (3·2^-20, 2^-20) lies EXACTLY on the line through (±3·2^20, ±2^20), but the
        // naive determinant of those coordinates is polluted by roundoff.
        var arrangement = new Arrangement2d(vertexSnapTolerance: 1e-30);
        var far0 = new Vector2d(-3 * Math.Pow(2, 20), -Math.Pow(2, 20));
        var far1 = new Vector2d(3 * Math.Pow(2, 20), Math.Pow(2, 20));
        var junction = new Vector2d(3 * Math.Pow(2, -20), Math.Pow(2, -20));

        arrangement.Insert(far0, far1);
        arrangement.Insert(junction, (0, 5));

        Assert.Equal(4, arrangement.VertexCount);
        Assert.Equal(3, arrangement.EdgeCount); // long segment split in two + the branch
    }

    [Fact]
    public void NestedIslands_AssignHolesThroughTheLevels()
    {
        var arrangement = new Arrangement2d();
        InsertBox(arrangement, (0, 0), (8, 8));
        InsertBox(arrangement, (2, 2), (6, 6));
        InsertBox(arrangement, (3, 3), (4, 4));

        var cells = arrangement.ExtractCells().OrderBy(c => c.Area).ToList();
        Assert.Equal(3, cells.Count);
        Assert.Equal(1.0, cells[0].Area, 12);              // innermost square
        Assert.Equal(16.0 - 1.0, cells[1].Area, 12);       // middle square minus innermost
        Assert.Equal(64.0 - 16.0, cells[2].Area, 12);      // outer minus middle
        Assert.Single(cells[1].Holes);
        Assert.Single(cells[2].Holes);
        Assert.Equal(64.0, cells.Sum(c => c.Area), 12);
    }

    // ---- edge broad phase ----
    //
    // Insertion narrows the edges it tests with a uniform grid over edge bounding boxes.
    // The failure mode of a spatial index is a MISSED candidate — an intersection that is
    // never found — so these tests check the arrangement's defining invariant directly
    // (no two edges cross away from a shared vertex; no vertex sits strictly inside an
    // edge) on inputs whose crossings are exactly representable, at sizes that force the
    // index to build and rebuild.

    /// <summary>
    /// The arrangement's defining property, verified with exact predicates. Only valid on
    /// inputs whose crossing points are exactly representable — otherwise the documented
    /// snap-tolerance rounding may legitimately perturb an edge.
    /// </summary>
    private static void AssertNoUnresolvedCrossings(Arrangement2d arrangement)
    {
        int edges = arrangement.EdgeCount;
        for (int i = 0; i < edges; i++)
        {
            var (ia, ib) = arrangement.EdgeAt(i);
            var p = arrangement.VertexAt(ia);
            var q = arrangement.VertexAt(ib);
            for (int j = i + 1; j < edges; j++)
            {
                var (ja, jb) = arrangement.EdgeAt(j);
                if (ia == ja || ia == jb || ib == ja || ib == jb)
                    continue; // adjacent edges legitimately touch
                var r = arrangement.VertexAt(ja);
                var s = arrangement.VertexAt(jb);
                bool straddles =
                    Predicates2d.Orient2dSign(p, q, r) * Predicates2d.Orient2dSign(p, q, s) < 0 &&
                    Predicates2d.Orient2dSign(r, s, p) * Predicates2d.Orient2dSign(r, s, q) < 0;
                Assert.False(straddles, $"edges {i} and {j} cross without a shared vertex");
            }
        }

        for (int i = 0; i < edges; i++)
        {
            var (ia, ib) = arrangement.EdgeAt(i);
            var p = arrangement.VertexAt(ia);
            var q = arrangement.VertexAt(ib);
            for (int v = 0; v < arrangement.VertexCount; v++)
            {
                if (v == ia || v == ib)
                    continue;
                var x = arrangement.VertexAt(v);
                if (Predicates2d.Orient2dSign(p, q, x) != 0)
                    continue;
                bool between = Math.Abs(q.X - p.X) >= Math.Abs(q.Y - p.Y)
                    ? (p.X < q.X ? p.X < x.X && x.X < q.X : q.X < x.X && x.X < p.X)
                    : (p.Y < q.Y ? p.Y < x.Y && x.Y < q.Y : q.Y < x.Y && x.Y < p.Y);
                Assert.False(between, $"vertex {v} sits strictly inside edge {i} (unsplit T-junction)");
            }
        }
    }

    [Fact]
    public void BroadPhase_DenseGrid_FindsEveryCrossing()
    {
        // 30 + 30 integer-coordinate lines: 900 crossings, ~1800 edges, so the index is
        // built and rebuilt several times during the insert sequence. Cell count and area
        // are known exactly.
        const int n = 30;
        var arrangement = new Arrangement2d();
        for (int i = 0; i < n; i++)
        {
            arrangement.Insert((0, i), (n - 1, i));
            arrangement.Insert((i, 0), (i, n - 1));
        }

        var cells = arrangement.ExtractCells();
        Assert.Equal((n - 1) * (n - 1), cells.Count);
        Assert.All(cells, c => Assert.Equal(1.0, c.Area, 12));
        AssertNoUnresolvedCrossings(arrangement);
    }

    [Fact]
    public void BroadPhase_SeparatedClusters_DoNotInterfere()
    {
        // Identical fine detail in two clusters a million units apart: the grid's cell
        // size is driven by the (short) edges, so the clusters land in far-apart cells —
        // the case where a naive fixed cell size either explodes or misses.
        var arrangement = new Arrangement2d();
        foreach (double offset in (ReadOnlySpan<double>)[0.0, 1e6])
        {
            for (int i = 0; i < 12; i++)
            {
                arrangement.Insert((offset, i * 0.25), (offset + 3, i * 0.25));
                arrangement.Insert((offset + i * 0.25, 0), (offset + i * 0.25, 3));
            }
        }

        var cells = arrangement.ExtractCells();
        Assert.Equal(2 * 11 * 11, cells.Count);
        Assert.All(cells, c => Assert.Equal(0.0625, c.Area, 12));
        AssertNoUnresolvedCrossings(arrangement);
    }

    [Fact]
    public void BroadPhase_OneLongSegmentAcrossManyShortOnes_StillSplitsEveryCrossing()
    {
        // A segment far longer than the grid cell goes into the always-scanned overflow
        // list; it must still be split by all 60 rungs it crosses.
        var arrangement = new Arrangement2d();
        for (int i = 0; i < 60; i++)
            arrangement.Insert((i, -1), (i, 1));
        arrangement.Insert((-5, 0), (100, 0));

        // The long segment is cut at every rung: 60 crossings plus its two free ends.
        int onAxis = 0;
        for (int v = 0; v < arrangement.VertexCount; v++)
        {
            if (arrangement.VertexAt(v).Y == 0)
                onAxis++;
        }
        Assert.Equal(62, onAxis);
        AssertNoUnresolvedCrossings(arrangement);
    }

    [Fact]
    public void LoneCellWhoseReversedPerimeterRoundsSmaller_KeepsItsWholeArea()
    {
        // Regression: this kite's outer walk and its reverse have shoelace areas that differ
        // by one ULP (the anchored fan sums the same triangles in a different order), and
        // every vertex of one loop is a vertex of the other, so the containment probe sat
        // exactly ON the cell boundary. The negative loop -- the unbounded face -- was
        // therefore adopted as the cell's own hole and cancelled it to ~1e-16.
        // Loops of the same connected component can never nest, which is now the rule.
        var arrangement = new Arrangement2d();
        arrangement.InsertPolyline([
            new Vector2d(-9.1848509936051479E-16, -5),
            new Vector2d(-0.57402514854763564, -6.38581929876693),
            new Vector2d(-1.2111442151154569E-15, -6.6235883004385911),
            new Vector2d(0.57402514854763331, -6.38581929876693),
        ], closed: true);

        var cell = Assert.Single(arrangement.ExtractCells());
        Assert.Empty(cell.Holes);
        Assert.Equal(0.9319805153394637, cell.Area, 12);
    }
}
