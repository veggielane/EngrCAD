using EngrCAD.Core;
using EngrCAD.Ecad;
using Xunit;

namespace EngrCAD.Ecad.Tests;

/// <summary>
/// Stage 3 — PCB placement constraints. The verification bar is higher than usual (ECAD fails
/// plausibly): a satisfied set converges to the weld tier with the DOF reported; an align-to-edge
/// makes the two edges EXACTLY collinear and parallel; a stated spacing is met exactly; a
/// clear-of-keepout leaves the footprint disjoint from the keep-out; over/under/contradiction/
/// stationary are each named; a failed solve leaves the layout bit-identical (through the
/// serializer); the solve is deterministic; and the one-declaration identity survives.
/// </summary>
public sealed class PcbConstraintTests
{
    private const double Weld = 1e-9;

    // ---- fixtures -----------------------------------------------------------

    private static PcbLayout MakeLayout(params (string Ref, double X, double Y, double Rot)[] parts)
    {
        var sch = new Schematic("placement");
        foreach (var (r, _, _, _) in parts)
            sch.Add(r, PcbFixtures.SmdResistor());
        var layout = new PcbLayout(sch, PcbFixtures.Board());
        foreach (var (r, x, y, rot) in parts)
            layout.Place(r, x, y, rot);
        return layout;
    }

    /// <summary>The footprint bounding-circle radius the solver models a placement's extent by
    /// (a pad's centre reach plus its half diagonal).</summary>
    private static double FootprintRadius()
    {
        // R_0805: pads at ±1, 1.2 × 1.4.
        return 1.0 + 0.5 * Math.Sqrt(1.2 * 1.2 + 1.4 * 1.4);
    }

    private static PcbPlacement Placed(PcbLayout layout, string reference) =>
        layout.Placements.First(p => p.Reference == reference);

    private static Vector2d World(PcbLayout layout, string reference, Vector2d local)
    {
        var p = Placed(layout, reference);
        double th = p.RotationDegrees * Math.PI / 180.0, c = Math.Cos(th), s = Math.Sin(th);
        return new Vector2d(p.X + c * local.X - s * local.Y, p.Y + s * local.X + c * local.Y);
    }

    private static Vector2d WorldDir(PcbLayout layout, string reference, Vector2d local)
    {
        double th = Placed(layout, reference).RotationDegrees * Math.PI / 180.0;
        double c = Math.Cos(th), s = Math.Sin(th);
        return new Vector2d(c * local.X - s * local.Y, s * local.X + c * local.Y);
    }

    // =====================================================================
    //  A satisfied set converges to the weld tier, with the DOF reported
    // =====================================================================

    [Fact]
    public void SatisfiedSet_ConvergesToWeldTier_AndReportsDof()
    {
        var layout = MakeLayout(("R1", 5, 0, 0), ("R2", 15, 3, 0));
        var result = layout.Constrain()
            .Lock("R1")
            .Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 10)
            .TrySolve();

        Assert.True(result.Converged, result.ToString());
        Assert.True(result.Residual <= Weld, $"residual {result.Residual}");
        Assert.NotNull(result.Solved);

        // one free body (R2, 3 DOF); Distance pins one; two remain (angle around R1, R2's spin).
        Assert.Equal(3, result.FreeDegreesOfFreedom);
        Assert.Equal(1, result.ConstrainedDegreesOfFreedom);
        Assert.Equal(2, result.RemainingDegreesOfFreedom);

        double d = (World(result.Solved!, "R2", Vector2d.Zero)
                    - World(result.Solved!, "R1", Vector2d.Zero)).Length;
        Assert.Equal(10.0, d, 8);   // the weld tier: a solve stops at residual ≤ 1e-9
        // the locked datum did not move
        Assert.Equal(5.0, Placed(result.Solved!, "R1").X, 12);
        Assert.Equal(0.0, Placed(result.Solved!, "R1").Y, 12);
    }

    // =====================================================================
    //  Align-to-edge: the two edges are EXACTLY collinear and parallel
    // =====================================================================

    [Fact]
    public void AlignEdge_MakesTheEdgesParallelAndCollinear()
    {
        var layout = MakeLayout(("R1", 0, 5, 25));   // drawn tilted, above the bottom edge
        var c = layout.Constrain();
        var boardBottom = c.BoardEdge(0);            // (-25,-20) → (25,-20), direction +X
        var result = c
            .AlignEdge(PcbLine.Component("R1", new Vector2d(0, -0.7), new Vector2d(1, 0)), boardBottom)
            .TrySolve();

        Assert.True(result.Converged, result.ToString());
        var solved = result.Solved!;

        // parallel — the component's +X and the board edge are collinear directions
        var dir = WorldDir(solved, "R1", new Vector2d(1, 0));
        Assert.Equal(0.0, dir.Cross(new Vector2d(1, 0)), 8);

        // collinear — the component's edge point lies exactly on the board edge line (y = −20)
        var point = World(solved, "R1", new Vector2d(0, -0.7));
        Assert.Equal(-20.0, point.Y, 8);

        // AlignEdge pins rotation + one translation; the slide along the edge is left free
        Assert.Equal(3, result.FreeDegreesOfFreedom);
        Assert.Equal(2, result.ConstrainedDegreesOfFreedom);
        Assert.Equal(1, result.RemainingDegreesOfFreedom);
    }

    [Fact]
    public void AlignEdge_WithGap_HoldsTheGapOnTheDrawnSide()
    {
        var layout = MakeLayout(("R1", 0, 5, 0));
        var c = layout.Constrain();
        var result = c
            .AlignEdge(PcbLine.Component("R1", new Vector2d(0, -0.7), new Vector2d(1, 0)),
                c.BoardEdge(0), gap: 3.0)
            .TrySolve();

        Assert.True(result.Converged, result.ToString());
        // the component edge point sits 3 mm above the board edge (its drawn side)
        Assert.Equal(-17.0, World(result.Solved!, "R1", new Vector2d(0, -0.7)).Y, 8);
    }

    // =====================================================================
    //  A stated spacing is met exactly (pad to pad)
    // =====================================================================

    [Fact]
    public void Spacing_BetweenPads_IsMetExactly()
    {
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 8, 0, 0));
        var c = layout.Constrain().Lock("R1");
        var a = c.Pad("R1", "2");
        var b = c.Pad("R2", "1");
        var result = c.Spacing(a, b, 5.0).TrySolve();

        Assert.True(result.Converged, result.ToString());
        var solved = result.Solved!;
        double gap = (World(solved, "R1", new Vector2d(1, 0)) - World(solved, "R2", new Vector2d(-1, 0))).Length;
        Assert.Equal(5.0, gap, 8);
    }

    // =====================================================================
    //  Clear-of-keepout leaves the footprint disjoint via the region query
    // =====================================================================

    [Fact]
    public void ClearOfRegion_LeavesTheFootprintDisjointFromTheKeepOut()
    {
        var poly = new[]
        {
            new Vector2d(-3, -3), new Vector2d(3, -3), new Vector2d(3, 3), new Vector2d(-3, 3),
        };
        var layout = MakeLayout(("R1", 1, 0.5, 0));   // drawn overlapping the keep-out
        double clearance = 1.0;
        var result = layout.Constrain().ClearOfRegion("R1", poly, clearance).TrySolve();

        Assert.True(result.Converged, result.ToString());
        var solved = result.Solved!;

        double r = FootprintRadius();
        // every pad is OUTSIDE the keep-out (disjoint via the region query)
        foreach (var pad in new[] { new Vector2d(-1, 0), new Vector2d(1, 0) })
            Assert.False(PcbGeometry.PolygonContains(poly, World(solved, "R1", pad)));

        // the footprint circle stands the full clearance clear of the keep-out
        double originDistance = NearestBoundaryDistance(poly, World(solved, "R1", Vector2d.Zero));
        Assert.True(originDistance >= r + clearance - 1e-6,
            $"origin {originDistance:g6} < radius {r:g6} + clearance {clearance}");
        Assert.Equal(r + clearance, originDistance, 6);   // pushed exactly to the boundary
    }

    [Fact]
    public void ClearOf_BetweenFootprints_PushesToExactlyTheClearance()
    {
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 1, 0, 0));   // overlapping
        double clearance = 0.5;
        var result = layout.Constrain().Lock("R1").ClearOf("R1", "R2", clearance).TrySolve();

        Assert.True(result.Converged, result.ToString());
        double d = (World(result.Solved!, "R2", Vector2d.Zero)
                    - World(result.Solved!, "R1", Vector2d.Zero)).Length;
        Assert.Equal(2 * FootprintRadius() + clearance, d, 6);
    }

    [Fact]
    public void InsideBoard_KeepsTheFootprintOnTheBoard()
    {
        var layout = MakeLayout(("R1", 30, 0, 0));   // drawn off the 50×40 board's right edge
        var result = layout.Constrain().InsideBoard("R1", margin: 0.5).TrySolve();

        Assert.True(result.Converged, result.ToString());
        double r = FootprintRadius();
        // the footprint circle sits at least (radius + margin) inside the board's right edge (x = 25)
        Assert.True(Placed(result.Solved!, "R1").X + r + 0.5 <= 25 + 1e-6,
            $"R1.X {Placed(result.Solved!, "R1").X:g6}");
    }

    // =====================================================================
    //  Under-constrained reports its DOF; over/contradiction/stationary named
    // =====================================================================

    [Fact]
    public void UnderConstrained_ReportsRemainingDof()
    {
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 5, 3, 0));
        var result = layout.Constrain()
            .Lock("R1")
            .AlignY(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"))
            .TrySolve();

        Assert.True(result.Converged, result.ToString());
        Assert.True(result.IsUnderConstrained);
        Assert.Equal(3, result.FreeDegreesOfFreedom);
        Assert.Equal(1, result.ConstrainedDegreesOfFreedom);
        Assert.Equal(2, result.RemainingDegreesOfFreedom);
        Assert.Contains(result.Diagnostics, d => d.Contains("degree") && d.Contains("freedom"));
        // AlignY was met: shared y
        Assert.Equal(Placed(result.Solved!, "R1").Y, Placed(result.Solved!, "R2").Y, 8);
    }

    [Fact]
    public void Contradiction_IsNamed_AndFailsLoudly()
    {
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 5, 0, 0));
        var c = layout.Constrain().Lock("R1");
        c.Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 5);
        c.Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 10);   // contradiction

        var result = c.TrySolve();
        Assert.False(result.Converged);
        Assert.True(result.IsOverConstrained);
        Assert.Null(result.Solved);
        Assert.Contains(result.Diagnostics, d => d.Contains("Distance") && d.Contains("off by"));

        Assert.Throws<PcbConstraintSolveException>(() => c.Solve());
    }

    [Fact]
    public void StationaryStart_IsNamedRatherThanNudged()
    {
        // Perpendicular on two directions that start exactly parallel: d(cos θ)/dθ = 0, so no
        // first-order motion improves the residual — the MateSolver stationary case.
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 5, 0, 0));
        var result = layout.Constrain()
            .Lock("R1")
            .Perpendicular(PlacementDirection.Axis("R1"), PlacementDirection.Axis("R2"))
            .TrySolve();

        Assert.False(result.Converged);
        Assert.Contains(result.Diagnostics, d => d.Contains("stationary"));
    }

    [Fact]
    public void RequireFullyConstrained_ThrowsOnRemainingDof()
    {
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 5, 3, 0));
        Assert.Throws<PcbConstraintSolveException>(() =>
            layout.Constrain()
                .Lock("R1")
                .AlignY(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"))
                .Solve(new PcbConstraintSolverSettings { RequireFullyConstrained = true }));
    }

    // =====================================================================
    //  A failed solve leaves the layout bit-identical (through the serializer)
    // =====================================================================

    [Fact]
    public void FailedSolve_LeavesTheLayoutBitIdentical()
    {
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 5, 0, 0));
        string before = layout.Save();

        var c = layout.Constrain().Lock("R1");
        c.Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 5);
        c.Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 10);   // unsatisfiable
        var result = c.TrySolve();

        Assert.False(result.Converged);
        Assert.Null(result.Solved);
        Assert.Equal(before, layout.Save());
    }

    // =====================================================================
    //  Determinism — one seed gives one solve, bit for bit
    // =====================================================================

    [Fact]
    public void Solve_IsDeterministic_ToTheBit()
    {
        static PcbConstraintSolveResult Run()
        {
            var layout = MakeLayout(("R1", 5, 0, 0), ("R2", 15, 3, 10), ("R3", -8, 12, 40));
            var c = layout.Constrain().Lock("R1");
            c.Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 10);
            c.Distance(PlacementPoint.Origin("R2"), PlacementPoint.Origin("R3"), 7);
            c.Orient("R3", 55);
            return c.TrySolve();
        }

        var a = Run();
        var b = Run();
        Assert.True(a.Converged && b.Converged);
        // the whole solved layout is byte-identical, so every pose matches to the last bit
        Assert.Equal(a.Solved!.Save(), b.Solved!.Save());
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Residual), BitConverter.DoubleToInt64Bits(b.Residual));
        foreach (var reference in new[] { "R2", "R3" })
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(Placed(a.Solved!, reference).X),
                BitConverter.DoubleToInt64Bits(Placed(b.Solved!, reference).X));
            Assert.Equal(BitConverter.DoubleToInt64Bits(Placed(a.Solved!, reference).RotationDegrees),
                BitConverter.DoubleToInt64Bits(Placed(b.Solved!, reference).RotationDegrees));
        }
    }

    // =====================================================================
    //  The one-declaration identity survives the solve
    // =====================================================================

    [Fact]
    public void Solve_PreservesTheOneDeclarationIdentity()
    {
        var layout = PcbFixtures.Layout();   // R1 (SMD) at (5,0), J1 (through-hole) at (-8,4,90), wired
        var drawnPad = World(layout, "R1", new Vector2d(-1, 0));   // R1 pad-1 before the move

        var result = layout.Constrain()
            .Lock("J1")
            .Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("J1"), 20)
            .TrySolve();

        Assert.True(result.Converged, result.ToString());
        var solved = result.Solved!;

        // the identity check still passes — the copper derives from the moved placements
        Assert.True(solved.Check().Ok, solved.Check().ToString());
        Assert.True(solved.Check().IdentityHolds);

        // PadsOfNet returns the MOVED copper
        var vcc = solved.Schematic.Nets.First(n => n.Name == "VCC");
        var pads = solved.PadsOfNet(vcc);
        Assert.Equal(2, pads.Count);   // J1.1 and R1.1
        Assert.NotEqual(Placed(layout, "R1").X, Placed(solved, "R1").X);   // R1 moved
        var movedPad = World(solved, "R1", new Vector2d(-1, 0));
        Assert.True((movedPad - drawnPad).Length > 1, "R1's copper did not move");
    }

    // =====================================================================
    //  Group / Cluster — relative poses locked (moves as one rigid body)
    // =====================================================================

    [Fact]
    public void Group_LocksRelativePoses_ThroughTranslationAndRotation()
    {
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 5, 0, 30));
        var result = layout.Constrain()
            .Group("R1", "R2")
            .Coincident(PlacementPoint.Origin("R1"), PlacementPoint.Board(new Vector2d(10, 8)))
            .Orient("R1", 90)
            .TrySolve();

        Assert.True(result.Converged, result.ToString());
        var solved = result.Solved!;

        // R1 landed on its target pose
        Assert.Equal(10.0, Placed(solved, "R1").X, 8);
        Assert.Equal(8.0, Placed(solved, "R1").Y, 8);
        Assert.Equal(90.0, Placed(solved, "R1").RotationDegrees, 8);

        // R2 followed RIGIDLY: the group rotated 90°, so its (5,0) offset became (0,5)
        Assert.Equal(10.0, Placed(solved, "R2").X, 8);
        Assert.Equal(13.0, Placed(solved, "R2").Y, 8);
        Assert.Equal(120.0, Placed(solved, "R2").RotationDegrees, 8);

        // the group is one 3-DOF body, fully pinned
        Assert.Equal(3, result.FreeDegreesOfFreedom);
        Assert.Equal(3, result.ConstrainedDegreesOfFreedom);
    }

    // =====================================================================
    //  Every threshold relative — a uniform scale still solves exactly
    // =====================================================================

    [Theory]
    [InlineData(1.0)]
    [InlineData(1000.0)]
    public void Solve_IsScaleInvariant(double scale)
    {
        var sch = new Schematic("scaled");
        sch.Add("R1", PcbFixtures.SmdResistor());
        sch.Add("R2", PcbFixtures.SmdResistor());
        var board = new PcbBoard(
            [
                new Vector2d(-25 * scale, -20 * scale), new Vector2d(25 * scale, -20 * scale),
                new Vector2d(25 * scale, 20 * scale), new Vector2d(-25 * scale, 20 * scale),
            ],
            thickness: 1.6 * scale);
        var layout = new PcbLayout(sch, board);
        layout.Place("R1", 0, 0, 0);
        layout.Place("R2", 8 * scale, 3 * scale, 0);

        double target = 10 * scale;
        var result = layout.Constrain()
            .Lock("R1")
            .Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), target)
            .TrySolve();

        Assert.True(result.Converged, result.ToString());
        double d = (World(result.Solved!, "R2", Vector2d.Zero)
                    - World(result.Solved!, "R1", Vector2d.Zero)).Length;
        Assert.True(Math.Abs(d - target) / target < 1e-9, $"relative error {(d - target) / target:g3}");
    }

    // =====================================================================
    //  Guards fire — bad references, empty groups, zero directions
    // =====================================================================

    [Fact]
    public void Constraint_OnUnplacedComponent_RefusesByName()
    {
        var layout = MakeLayout(("R1", 0, 0, 0));
        var ex = Assert.Throws<ArgumentException>(() =>
            layout.Constrain().Distance(PlacementPoint.Origin("R99"), PlacementPoint.Origin("R1"), 5));
        Assert.Contains("R99", ex.Message);
    }

    [Fact]
    public void Pad_WithUnknownNumber_RefusesByName()
    {
        var layout = MakeLayout(("R1", 0, 0, 0));
        var ex = Assert.Throws<ArgumentException>(() => layout.Constrain().Pad("R1", "9"));
        Assert.Contains("pad '9'", ex.Message);
    }

    [Fact]
    public void Group_WithOneMember_RefusesByName()
    {
        var layout = MakeLayout(("R1", 0, 0, 0));
        Assert.Throws<ArgumentException>(() => layout.Constrain().Group("R1"));
    }

    [Fact]
    public void ZeroDirection_RefusesByName()
    {
        Assert.Throws<ArgumentException>(() => PlacementDirection.Board(Vector2d.Zero));
    }

    [Fact]
    public void UnmentionedPlacement_IsReportedAndLeftInPlace()
    {
        var layout = MakeLayout(("R1", 0, 0, 0), ("R2", 5, 0, 0), ("R3", -5, 5, 0));
        var result = layout.Constrain()
            .Lock("R1")
            .Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("R2"), 8)
            .TrySolve();

        Assert.True(result.Converged);
        Assert.Contains(result.Diagnostics, d => d.Contains("no constraint mentions") && d.Contains("R3"));
        // R3 stayed exactly where it was drawn
        Assert.Equal(-5.0, Placed(result.Solved!, "R3").X, 12);
        Assert.Equal(5.0, Placed(result.Solved!, "R3").Y, 12);
    }

    // =====================================================================
    //  Persistence — extends the stage-2 seam, a byte fixed point
    // =====================================================================

    [Fact]
    public void NoConstraints_SaveIsByteIdenticalToStage2()
    {
        var layout = PcbFixtures.Layout();
        Assert.Equal(layout.Save(), layout.Constrain().Save());
    }

    [Fact]
    public void ConstrainedLayout_IsASaveLoadSaveFixedPoint()
    {
        var layout = PcbFixtures.Layout();
        var c = layout.Constrain();
        c.Lock("J1");
        c.Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("J1"), 15);
        c.Orient("R1", 45);
        c.Parallel(PlacementDirection.Axis("R1"), PlacementDirection.Board(new Vector2d(1, 0)));
        c.Perpendicular(PlacementDirection.Axis("R1"), PlacementDirection.Axis("J1"));
        c.AlignX(PlacementPoint.Origin("R1"), PlacementPoint.Origin("J1"));
        c.AlignEdge(PcbLine.Component("R1", new Vector2d(0, -0.7), new Vector2d(1, 0)), c.BoardEdge(0), 2);
        c.PointOnLine(PlacementPoint.Origin("R1"), c.BoardEdge(1), 3);
        c.InsideBoard("R1", 1.0);
        c.ClearOf("R1", "J1", 0.5);
        var poly = new[]
        {
            new Vector2d(-3, -3), new Vector2d(3, -3), new Vector2d(3, 3), new Vector2d(-3, 3),
        };
        c.ClearOfRegion("R1", poly, 0.5);

        string s1 = c.Save();
        var reloaded = ConstrainedLayout.Load(s1, PcbFixtures.Library());
        string s2 = reloaded.Save();

        Assert.Equal(s1, s2);
        Assert.Equal(c.Constraints.Count, reloaded.Constraints.Count);
        Assert.NotEqual(layout.Save(), s1);   // the constraints really are in the file
    }

    [Fact]
    public void PersistedConstraints_SolveTheSameAfterReload()
    {
        var layout = PcbFixtures.Layout();
        var c = layout.Constrain()
            .Lock("J1")
            .Distance(PlacementPoint.Origin("R1"), PlacementPoint.Origin("J1"), 18);

        var direct = c.TrySolve();
        var reloaded = ConstrainedLayout.Load(c.Save(), PcbFixtures.Library()).TrySolve();

        Assert.True(direct.Converged && reloaded.Converged);
        Assert.Equal(direct.Solved!.Save(), reloaded.Solved!.Save());
    }

    // ---- test-side geometry -------------------------------------------------

    /// <summary>The distance from a point to a polygon's boundary (an independent reimplementation,
    /// so the disjointness assertion does not share the solver's own arithmetic).</summary>
    private static double NearestBoundaryDistance(IReadOnlyList<Vector2d> polygon, Vector2d p)
    {
        double best = double.PositiveInfinity;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];
            var ab = b - a;
            double t = ab.LengthSquared > 0 ? Math.Clamp((p - a).Dot(ab) / ab.LengthSquared, 0, 1) : 0;
            best = Math.Min(best, (p - (a + ab * t)).Length);
        }
        return best;
    }
}
