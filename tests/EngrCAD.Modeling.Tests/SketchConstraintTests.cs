using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The variational sketch constraint solver: each constraint solo, combinations, the
/// fully-constrained rectangle-with-fillets, under-constrained minimal change,
/// contradiction naming, scale freedom, and the named stationary configuration —
/// the MateSolver verification pattern applied to 2D.
/// </summary>
public class SketchConstraintTests
{
    private const double Weld = 1e-9;

    /// <summary>A deliberately sloppy quadrilateral: nothing square, corners off.</summary>
    private static Sketch SloppyQuad() => Sketch.Start(0, 0.4)
        .LineTo(10.2, 0)
        .LineTo(10.5, 7.8)
        .LineTo(-0.3, 8.2)
        .Close();

    private static IReadOnlyList<Curve2d> Curves(SketchSolveResult result) =>
        result.Sketch!.ToCurves();

    private static Line2d LineOf(SketchSolveResult result, int segment) =>
        (Line2d)Curves(result)[segment];

    private static Arc2d ArcOf(SketchSolveResult result, int segment) =>
        (Arc2d)Curves(result)[segment];

    // ----------------------------------------------------------- solo constraints

    [Fact]
    public void Horizontal_LevelsTheLine()
    {
        var cs = SloppyQuad().Constrain();
        var result = cs.Horizontal(cs.Line(0)).Solve();

        Assert.True(result.Converged);
        var line = LineOf(result, 0);
        Assert.Equal(line.Start.Y, line.End.Y, Weld);
        Assert.True(result.IsUnderConstrained);   // everything else is still free
    }

    [Fact]
    public void Vertical_PlumbsTheLine()
    {
        var cs = SloppyQuad().Constrain();
        var result = cs.Vertical(cs.Line(1)).Solve();

        Assert.True(result.Converged);
        var line = LineOf(result, 1);
        Assert.Equal(line.Start.X, line.End.X, Weld);
    }

    [Fact]
    public void HorizontalPoints_LevelsThePair()
    {
        var cs = SloppyQuad().Constrain();
        var result = cs.Horizontal(cs.Point(0), cs.Point(1)).Solve();

        Assert.True(result.Converged);
        var line = LineOf(result, 0);
        Assert.Equal(line.Start.Y, line.End.Y, Weld);
    }

    [Fact]
    public void Parallel_AlignsOppositeSides()
    {
        var cs = SloppyQuad().Constrain();
        var result = cs.Parallel(cs.Line(0), cs.Line(2)).Solve();

        Assert.True(result.Converged);
        var a = LineOf(result, 0);
        var b = LineOf(result, 2);
        var da = (a.End - a.Start).Normalized();
        var db = (b.End - b.Start).Normalized();
        Assert.Equal(0, da.Cross(db), Weld);
    }

    [Fact]
    public void Perpendicular_SquaresTheCorner()
    {
        var cs = SloppyQuad().Constrain();
        var result = cs.Perpendicular(cs.Line(0), cs.Line(1)).Solve();

        Assert.True(result.Converged);
        var a = LineOf(result, 0);
        var b = LineOf(result, 1);
        var da = (a.End - a.Start).Normalized();
        var db = (b.End - b.Start).Normalized();
        Assert.Equal(0, da.Dot(db), Weld);
    }

    [Fact]
    public void Angle_SetsTheOpeningAngle()
    {
        double angle = Math.PI / 3;
        var cs = SloppyQuad().Constrain();
        var result = cs.Angle(cs.Line(0), cs.Line(1), angle).Solve();

        Assert.True(result.Converged);
        var a = LineOf(result, 0);
        var b = LineOf(result, 1);
        var da = (a.End - a.Start).Normalized();
        var db = (b.End - b.Start).Normalized();
        Assert.Equal(Math.Cos(angle), da.Dot(db), 1e-8);   // residual is (dot − cos)·L
    }

    [Fact]
    public void Angle_OfZero_IsRefusedTowardParallel()
    {
        var cs = SloppyQuad().Constrain();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => cs.Angle(cs.Line(0), cs.Line(2), 0));
        Assert.Contains("Parallel", ex.Message);
    }

    [Fact]
    public void EqualLength_EqualizesTheSides()
    {
        var cs = SloppyQuad().Constrain();
        var result = cs.EqualLength(cs.Line(0), cs.Line(1)).Solve();

        Assert.True(result.Converged);
        var a = LineOf(result, 0);
        var b = LineOf(result, 1);
        Assert.Equal((a.End - a.Start).Length, (b.End - b.Start).Length, Weld);
    }

    [Fact]
    public void Coincident_PinsWedgeVertexToArcCenter()
    {
        // A pac-man: a 270° arc about (0,0) with the wedge vertex drawn sloppily off
        // the center; Coincident snaps the joint onto the arc's center.
        var sketch = Sketch.Start(5, 0)
            .ArcTo(new(0, -5), 5, clockwise: false, largeArc: true)
            .LineTo(0.2, -0.1)
            .Close();
        var cs = sketch.Constrain();
        var result = cs.Coincident(cs.Point(2), cs.CenterOf(cs.Arc(0))).Solve();

        Assert.True(result.Converged);
        var arc = ArcOf(result, 0);
        var wedge = LineOf(result, 1).End;
        Assert.Equal(arc.Center.X, wedge.X, Weld);
        Assert.Equal(arc.Center.Y, wedge.Y, Weld);
    }

    [Fact]
    public void Distance_PointToPoint_SetsTheLength()
    {
        var cs = SloppyQuad().Constrain();
        var result = cs.Distance(cs.Point(0), cs.Point(1), 8).Solve();

        Assert.True(result.Converged);
        var line = LineOf(result, 0);
        Assert.Equal(8, (line.End - line.Start).Length, Weld);
    }

    [Fact]
    public void Distance_PointToLine_SetsTheHeight()
    {
        var cs = SloppyQuad().Constrain();
        var result = cs.Distance(cs.Point(2), cs.Line(0), 5).Solve();

        Assert.True(result.Converged);
        var line = LineOf(result, 0);
        var p = LineOf(result, 2).Start;   // joint 2 = start of segment 2
        var hat = (line.End - line.Start).Normalized();
        Assert.Equal(5, Math.Abs(hat.Cross(p - line.Start)), Weld);
    }

    [Fact]
    public void Fix_PinsThePointExactly()
    {
        var drawn = SloppyQuad();
        var cs = drawn.Constrain();
        var result = cs
            .Fix(cs.Point(0))
            .Horizontal(cs.Line(0))
            .Distance(cs.Point(0), cs.Point(1), 8)
            .Solve();

        Assert.True(result.Converged);
        var line = LineOf(result, 0);
        Assert.Equal(0, line.Start.X, Weld);
        Assert.Equal(0.4, line.Start.Y, Weld);
        Assert.Equal(line.Start.Y, line.End.Y, Weld);
        Assert.Equal(8, Math.Abs(line.End.X - line.Start.X), Weld);
    }

    [Fact]
    public void TangentLineArc_AndEqualRadius_TrueUpASloppySlot()
    {
        // A slot drawn with mismatched, non-tangent end radii.
        var drawn = Sketch.Start(-5, -2)
            .LineTo(5, -2)
            .ArcTo(new(5, 2), 2.6, clockwise: false)
            .LineTo(-5, 2)
            .ArcTo(new(-5, -2), 2.4, clockwise: false)
            .Close();
        var cs = drawn.Constrain();
        var result = cs
            .Tangent(cs.Line(0), cs.Arc(1))
            .Tangent(cs.Line(2), cs.Arc(1))
            .Tangent(cs.Line(2), cs.Arc(3))
            .Tangent(cs.Line(0), cs.Arc(3))
            .EqualRadius(cs.Arc(1), cs.Arc(3))
            .Solve();

        Assert.True(result.Converged);
        Assert.True(result.IsUnderConstrained);   // no dimensions: proportions stay drawn

        var bottom = LineOf(result, 0);
        var right = ArcOf(result, 1);
        var left = ArcOf(result, 3);
        var hat = (bottom.End - bottom.Start).Normalized();
        Assert.Equal(right.Radius, Math.Abs(hat.Cross(right.Center - bottom.Start)), Weld);
        Assert.Equal(left.Radius, Math.Abs(hat.Cross(left.Center - bottom.Start)), Weld);
        Assert.Equal(right.Radius, left.Radius, Weld);

        // Minimal change: the drawn proportions survive (LM steps lie in the row space
        // of the Jacobian, so unconstrained motions are never taken). The correction
        // splits between the joints and the centers — the drawn corners sat at x = ±5
        // with arc centers at ±3.34, and the solve meets in between — so the solved
        // slot keeps roughly the drawn size rather than an exact number.
        Assert.InRange((right.Center - left.Center).Length, 8.0, 11.0);
        Assert.Equal(2.0, right.Radius, 0.3);
    }

    [Fact]
    public void TangentArcArc_External_ClosesTheGap()
    {
        var drawn = Sketch.Rectangle(12, 8)
            .WithHole(Sketch.Circle(new(-2, 0), 1.5))
            .WithHole(Sketch.Circle(new(2.2, 0), 1.4));
        var cs = drawn.Constrain();
        var result = cs.Tangent(cs.HoleArc(0, 0), cs.HoleArc(1, 0)).Solve();

        Assert.True(result.Converged);
        var a = (Arc2d)result.Sketch!.Holes[0].ToCurves()[0];
        var b = (Arc2d)result.Sketch!.Holes[1].ToCurves()[0];
        Assert.Equal(a.Radius + b.Radius, (a.Center - b.Center).Length, Weld);
    }

    [Fact]
    public void ConcentricAndDimensions_TrueUpAWasher()
    {
        var drawn = Sketch.Circle(10).WithHole(Sketch.Circle(new(0.3, -0.2), 3.7));
        var cs = drawn.Constrain();
        var result = cs
            .Concentric(cs.Arc(0), cs.HoleArc(0, 0))
            .Radius(cs.Arc(0), 10)
            .Diameter(cs.HoleArc(0, 0), 8)
            .Fix(cs.CenterOf(cs.Arc(0)))
            .Solve();

        Assert.True(result.IsFullyConstrained);
        Assert.Equal(0, result.RemainingDegreesOfFreedom);

        var outer = ArcOf(result, 0);
        var hole = (Arc2d)result.Sketch!.Holes[0].ToCurves()[0];
        Assert.Equal(outer.Center.X, hole.Center.X, Weld);
        Assert.Equal(outer.Center.Y, hole.Center.Y, Weld);
        Assert.Equal(10, outer.Radius, Weld);
        Assert.Equal(4, hole.Radius, Weld);
        Assert.Equal(Math.PI * (100 - 16), result.Sketch!.Area(), 1e-8);
    }

    // ------------------------------------------------- the classic full constraint

    /// <summary>A sloppy hand-drawn rounded rectangle (radii disagree, nothing square)
    /// in the RoundedRectangle segment order: arc, right, arc, top, arc, left, arc,
    /// bottom (the closing line).</summary>
    private static Sketch SloppyRoundedRectangle() => Sketch.Start(12.8, -10.3)
        .ArcTo(new(15.3, -8.1), 2.3, clockwise: false)
        .LineTo(15.1, 7.6)
        .ArcTo(new(12.9, 10.2), 1.8, clockwise: false)
        .LineTo(-12.7, 10.4)
        .ArcTo(new(-15.2, 8.2), 2.1, clockwise: false)
        .LineTo(-14.8, -7.7)
        .ArcTo(new(-13.1, -9.8), 2.0, clockwise: false)
        .Close();

    private static ConstrainedSketch ConstrainRoundedRectangle(Sketch drawn)
    {
        var cs = drawn.Constrain();
        cs.Vertical(cs.Line(1)).Horizontal(cs.Line(3)).Vertical(cs.Line(5)).Horizontal(cs.Line(7))
          .Tangent(cs.Line(7), cs.Arc(0)).Tangent(cs.Line(1), cs.Arc(0))
          .Tangent(cs.Line(1), cs.Arc(2)).Tangent(cs.Line(3), cs.Arc(2))
          .Tangent(cs.Line(3), cs.Arc(4)).Tangent(cs.Line(5), cs.Arc(4))
          .Tangent(cs.Line(5), cs.Arc(6)).Tangent(cs.Line(7), cs.Arc(6))
          .EqualRadius(cs.Arc(0), cs.Arc(2))
          .EqualRadius(cs.Arc(0), cs.Arc(4))
          .EqualRadius(cs.Arc(0), cs.Arc(6))
          .Radius(cs.Arc(0), 2)
          .Distance(cs.Point(1), cs.Point(2), 16)   // right edge = height − 2r
          .Distance(cs.Point(3), cs.Point(4), 26)   // top edge = width − 2r
          .Fix(cs.Point(0));
        return cs;
    }

    [Fact]
    public void RectangleWithFillets_FullyConstrained_ZeroDofAndExactArea()
    {
        var result = ConstrainRoundedRectangle(SloppyRoundedRectangle()).Solve();

        Assert.True(result.Converged);
        Assert.Equal(0, result.RemainingDegreesOfFreedom);
        Assert.True(result.IsFullyConstrained);
        Assert.Equal(0, result.RedundantConstraintRows);

        // 30 × 20 with r = 2 fillets: area = w·h − (4 − π)·r². Every joint sits within
        // the 1e-9 residual tolerance of the exact shape, so the area holds to about
        // perimeter × 1e-9 ≈ 1e-7.
        Assert.Equal(30 * 20 - (4 - Math.PI) * 4, result.Sketch!.Area(), 1e-6);

        // Tangency is genuinely G1: every arc meets its lines with matched tangents,
        // which the exact Area identity above already needs; spot-check a corner.
        var bottom = LineOf(result, 7);
        var corner = ArcOf(result, 0);
        Assert.Equal(2, corner.Radius, Weld);
        Assert.Equal(2, Math.Abs(bottom.Start.Y - corner.Center.Y), 1e-8);
    }

    [Fact]
    public void RectangleWithFillets_SolvedSketch_FeedsThePipelineUnchanged()
    {
        var solved = ConstrainRoundedRectangle(SloppyRoundedRectangle()).Solve().Sketch!;

        // The solved sketch is an ordinary Sketch: regions, SDF, extrusion all work.
        var regions = solved.ToRegions();
        Assert.Single(regions);
        var mesh = Shape.Extrude(solved, 5).ToMesh();
        Assert.True(mesh.IsClosed);
        double area = 30 * 20 - (4 - Math.PI) * 4;
        Assert.Equal(area * 5, mesh.Volume(), area * 5 * 0.001);
    }

    // ------------------------------------------------------- refusing loudly

    [Fact]
    public void ContradictoryDistances_FailLoudly_NamingTheConstraint()
    {
        var cs = SloppyQuad().Constrain();
        cs.Distance(cs.Point(0), cs.Point(1), 10)
          .Distance(cs.Point(0), cs.Point(1), 12);

        var ex = Assert.Throws<SketchSolveException>(() => cs.Solve());
        Assert.False(ex.Result.Converged);
        Assert.Null(ex.Result.Sketch);
        Assert.Contains(ex.Result.Diagnostics, d => d.Contains("Distance(outer point 0, outer point 1)"));
        Assert.Contains(ex.Result.Diagnostics, d => d.Contains("the drawn sketch is unchanged"));

        // TrySolve reports the same failure without throwing, and produces nothing.
        var result = cs.TrySolve();
        Assert.False(result.Converged);
        Assert.Null(result.Sketch);
    }

    [Fact]
    public void PerpendicularOnExactlyParallelLines_NamesTheStationaryConfiguration()
    {
        // Rectangle: top and bottom are drawn EXACTLY parallel, so d/dθ cos θ = 0 and
        // no first-order step exists — the solver says so instead of nudging at random.
        var cs = Sketch.Rectangle(10, 6).Constrain();
        cs.Perpendicular(cs.Line(0), cs.Line(2));

        var result = cs.TrySolve();
        Assert.False(result.Converged);
        Assert.Null(result.Sketch);
        Assert.Contains(result.Diagnostics, d => d.Contains("stationary"));
    }

    [Fact]
    public void RedundantButConsistent_ConvergesAndReportsTheRedundancy()
    {
        // An exact rectangle with H+H+V+V plus a Parallel that those already imply:
        // 5 rows, rank 4 — over-constrained but consistent.
        var cs = Sketch.Rectangle(10, 6).Constrain();
        cs.Horizontal(cs.Line(0)).Horizontal(cs.Line(2))
          .Vertical(cs.Line(1)).Vertical(cs.Line(3))
          .Parallel(cs.Line(0), cs.Line(2));

        var result = cs.Solve();
        Assert.True(result.Converged);
        Assert.Equal(1, result.RedundantConstraintRows);
        Assert.Contains(result.Diagnostics, d => d.Contains("redundant but consistent"));
    }

    [Fact]
    public void DegenerateSolvedGeometry_IsRefusedNotReturned()
    {
        // Purely LINEAR constraints flattening the quad onto one horizontal line:
        // Gauss–Newton satisfies them to machine precision in one step, and the
        // configuration they describe encloses no area — the rebuild must refuse
        // rather than hand back a degenerate sketch.
        var drawn = Sketch.Start(0, 0)
            .LineTo(4, 0.2)
            .LineTo(4.1, 3)
            .LineTo(-0.2, 3.2)
            .Close();
        var cs = drawn.Constrain();
        cs.Fix(cs.Point(0))
          .Horizontal(cs.Point(0), cs.Point(1))
          .Horizontal(cs.Point(0), cs.Point(2))
          .Horizontal(cs.Point(0), cs.Point(3));

        // The default 1e-9 tolerance leaves y offsets of up to 1e-9, and a 4 × 1e-9
        // sliver is a LEGAL sketch under the scale-free degeneracy rule — tightening
        // the tolerance drives the linear rows to machine precision, where the flat
        // configuration genuinely encloses no area.
        var result = cs.TrySolve(new SketchSolverSettings { Tolerance = 1e-14 });
        Assert.False(result.Converged);
        Assert.Null(result.Sketch);
        Assert.Contains(result.Diagnostics, d => d.Contains("degenerate"));
    }

    // ------------------------------------------------------------ scale freedom

    [Theory]
    [InlineData(1e-3)]
    [InlineData(1.0)]
    [InlineData(1e3)]
    public void ScaleFreedom_SameSketchSolvesAtAnyScale(double s)
    {
        var drawn = Sketch.Start(0, 0.04 * s)
            .LineTo(10.2 * s, 0)
            .LineTo(10.5 * s, 7.8 * s)
            .LineTo(-0.3 * s, 8.2 * s)
            .Close();
        var cs = drawn.Constrain();
        var result = cs
            .Fix(cs.Point(0))
            .Horizontal(cs.Line(0))
            .Vertical(cs.Line(1))
            .Horizontal(cs.Line(2))
            .Vertical(cs.Line(3))
            .Distance(cs.Point(0), cs.Point(1), 10 * s)
            .Distance(cs.Point(1), cs.Point(2), 6 * s)
            .Solve();

        Assert.True(result.Converged);
        Assert.Equal(0, result.RemainingDegreesOfFreedom);
        // The residual tolerance is the absolute weld tier at every scale (the
        // MateSolver 0.01×/1×/1000× pattern); the AREA inherits it through the
        // dimensions, error ≤ perimeter × 1e-9 = 3.2e-8·s (asserted with 2× slack).
        Assert.Equal(60 * s * s, result.Sketch!.Area(), 6.4e-8 * s);
    }

    // ------------------------------------------------------------- odds and ends

    [Fact]
    public void BezierSegments_RideAlongWithTheirChord()
    {
        // Constraints touch only lines; the bézier's control points follow the chord
        // similarity and the solved sketch is still a valid closed loop.
        var drawn = Sketch.Start(0, 0)
            .LineTo(10, 0.3)
            .LineTo(10.2, 6)
            .BezierTo(new(6, 9), new(3, 9), new(0.1, 6.2))
            .Close();
        var cs = drawn.Constrain();
        var result = cs
            .Fix(cs.Point(0))
            .Horizontal(cs.Line(0))
            .Vertical(cs.Line(1))
            .Solve();

        Assert.True(result.Converged);
        Assert.True(result.Sketch!.Area() > 0);
        var line0 = LineOf(result, 0);
        Assert.Equal(line0.Start.Y, line0.End.Y, Weld);
        var bezier = (BezierCurve2d)Curves(result)[2];
        Assert.Equal(3, bezier.Degree);
    }

    [Fact]
    public void NoConstraints_ReturnsTheSketchAsDrawn()
    {
        var drawn = SloppyQuad();
        var result = drawn.Constrain().TrySolve();

        Assert.True(result.Converged);
        Assert.Same(drawn, result.Sketch);
        Assert.Equal(0, result.ConstrainedDegreesOfFreedom);
        Assert.Equal(8, result.FreeDegreesOfFreedom);   // 4 joints × 2
    }

    [Fact]
    public void FullCircle_HasNoJoints_AndSaysSo()
    {
        var cs = Sketch.Circle(5).Constrain();
        var ex = Assert.Throws<ArgumentException>(() => cs.Point(0));
        Assert.Contains("full circle", ex.Message);
    }

    [Fact]
    public void LineRef_OnAnArc_IsRefusedByName()
    {
        var cs = Sketch.Slot(10, 4).Constrain();
        var ex = Assert.Throws<ArgumentException>(() => cs.Line(1));   // slot segment 1 is an arc
        Assert.Contains("Arc(1)", ex.Message);
    }

    [Fact]
    public void ForeignReference_IsRefused()
    {
        var a = SloppyQuad().Constrain();
        var b = SloppyQuad().Constrain();
        var ex = Assert.Throws<ArgumentException>(() => a.Horizontal(b.Line(0)));
        Assert.Contains("different ConstrainedSketch", ex.Message);
    }

    [Fact]
    public void ReSolving_AlwaysSeedsFromTheDrawing()
    {
        var cs = SloppyQuad().Constrain();
        cs.Horizontal(cs.Line(0));
        var first = cs.Solve();
        cs.Vertical(cs.Line(1));
        var second = cs.Solve();

        Assert.True(first.Converged);
        Assert.True(second.Converged);
        var line = LineOf(second, 1);
        Assert.Equal(line.Start.X, line.End.X, Weld);
        // The first solve's result is untouched by the second.
        var firstLine = LineOf(first, 0);
        Assert.Equal(firstLine.Start.Y, firstLine.End.Y, Weld);
    }

    [Fact]
    public void DofReport_CountsSlotFreedomHonestly()
    {
        // Slot: 4 joints (8) + 2 arcs (6) = 14 variables. Tangencies + equal radius
        // are 5 rows, internal consistency 4 — the report separates what is pinned
        // from what stays free.
        var drawn = Sketch.Start(-5, -2)
            .LineTo(5, -2)
            .ArcTo(new(5, 2), 2.6, clockwise: false)
            .LineTo(-5, 2)
            .ArcTo(new(-5, -2), 2.4, clockwise: false)
            .Close();
        var cs = drawn.Constrain();
        var result = cs
            .Tangent(cs.Line(0), cs.Arc(1))
            .Tangent(cs.Line(2), cs.Arc(1))
            .Tangent(cs.Line(2), cs.Arc(3))
            .Tangent(cs.Line(0), cs.Arc(3))
            .EqualRadius(cs.Arc(1), cs.Arc(3))
            .Solve();

        Assert.True(result.Converged);
        Assert.Equal(14, result.FreeDegreesOfFreedom);
        Assert.Equal(9, result.ConstrainedDegreesOfFreedom);   // 5 user + 4 internal, independent
        Assert.Equal(5, result.RemainingDegreesOfFreedom);     // position 2 + angle + length + radius
        Assert.Contains(result.Diagnostics, d => d.Contains("degrees of freedom remain"));
    }
}
