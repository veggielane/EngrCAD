using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// The two carriers the constraint vocabulary used to lack — a cubic bézier and an
/// elliptical arc — plus the end tangency they share a mechanism with.
/// <para>The backlog filed both as one problem ("the nearest point is itself a solve, so the
/// residual needs its own foot parameter as a VARIABLE"). That is right for the bézier and
/// WRONG for the ellipse, and the correction is the interesting half: an ellipse is a CONIC,
/// so membership is closed form even though distance is a quartic root — a constraint needs
/// membership.</para>
/// </summary>
public class SketchCurveConstraintTests
{
    /// <summary>A closed loop: a straight base, a riser and a cubic arch back to the start.
    /// THREE segments on purpose — with only a base and an arch the two share both joints,
    /// so the arch's end tangent relative to the base is a constant no motion can change,
    /// which the solver correctly reports as a stationary configuration.</summary>
    private static Sketch ArchWithBezier() =>
        Sketch.Start(0, 0)
            .LineTo((40, 0))
            .LineTo((40, 10))
            .BezierTo((30, 30), (10, 30), (0, 0))
            .Close();

    /// <summary>A closed loop: a straight base and an elliptical arch over it.</summary>
    private static Sketch ArchWithEllipse() =>
        Sketch.Start(-20, 0)
            .LineTo((20, 0))
            .EllipticalArcTo((-20, 0), 20, 12, 0, largeArc: false, clockwise: false)
            .Close();

    // ---- point on an elliptical carrier ----

    /// <summary>
    /// The residual is signed and closed form, so a point drawn OFF the ellipse is pulled
    /// onto it — measured by the solved sketch's own exact signed distance, which knows
    /// nothing about the constraint that put it there.
    /// </summary>
    [Fact]
    public void APointIsPulledOntoAnEllipticalCarrier()
    {
        var drawn = ArchWithEllipse().WithHole(Sketch.Circle(new Vector2d(0, 6), 2));
        var cs = drawn.Constrain();
        // The circular hole's centre, dragged onto the elliptical arch.
        var hole = cs.HoleArc(0, 0);
        cs.PointOn(cs.CenterOf(hole), cs.Curve(1))
          .Fix(cs.Point(0))
          .Fix(cs.Point(1));

        var result = cs.TrySolve();
        Assert.True(result.Converged, result.ToString());
        var solved = result.Sketch!;

        // Where did the centre land, and is it ON the ellipse? Read it off the SOLVED
        // sketch's own geometry rather than off the solver.
        var ellipse = SolvedEllipse(solved);
        var centre = SolvedHoleCentre(solved);
        Assert.Equal(1.0, Normalized(ellipse, centre), 1e-9);

        // And the arch itself did not move: both its joints were fixed, and an ellipse
        // rides them.
        Assert.Equal(-20, solved.Segments[0].Start.X, 1e-9);
        Assert.Equal(20, solved.Segments[1].Start.X, 1e-9);
    }

    /// <summary>
    /// The carrier MOVES with its joints, which is what separates a real constraint from a
    /// test against fixed drawn geometry: stretch the arch by dimensioning its base and the
    /// constrained point rides the new ellipse.
    /// </summary>
    [Fact]
    public void TheEllipticalCarrierRidesItsJoints()
    {
        var drawn = ArchWithEllipse().WithHole(Sketch.Circle(new Vector2d(0, 6), 2));
        var cs = drawn.Constrain();
        cs.PointOn(cs.CenterOf(cs.HoleArc(0, 0)), cs.Curve(1))
          .Fix(cs.Point(0))
          .Distance(cs.Point(0), cs.Point(1), 60)   // was 40: the arch stretches
          .Horizontal(cs.Line(0));

        var result = cs.TrySolve();
        Assert.True(result.Converged, result.ToString());
        var solved = result.Sketch!;

        Assert.Equal(60, (solved.Segments[1].Start - solved.Segments[0].Start).Length, 1e-9);
        Assert.Equal(1.0, Normalized(SolvedEllipse(solved), SolvedHoleCentre(solved)), 1e-9);
    }

    /// <summary>A point drawn AT the ellipse's centre has magnitude but no gradient
    /// DIRECTION — the same singularity <c>PointOn(point, arc)</c> refuses at an arc's
    /// centre, and it is refused the same way rather than left to stall.</summary>
    [Fact]
    public void APointAtTheEllipseCentreIsRefused()
    {
        var drawn = ArchWithEllipse().WithHole(Sketch.Circle(new Vector2d(0, 0), 1));
        var cs = drawn.Constrain();
        var thrown = Assert.Throws<ArgumentException>(
            () => cs.PointOn(cs.CenterOf(cs.HoleArc(0, 0)), cs.Curve(1)));
        Assert.Contains("no gradient direction", thrown.Message);
    }

    // ---- point on a bézier carrier ----

    /// <summary>
    /// The bézier's foot is a real unknown, so the constraint removes exactly ONE degree of
    /// freedom (two rows against one new variable) — which is what a point-on-curve
    /// constraint means, and is asserted directly off the solver's own DOF report.
    /// </summary>
    [Fact]
    public void APointIsPulledOntoABezierCarrier()
    {
        var drawn = ArchWithBezier().WithHole(Sketch.Circle(new Vector2d(20, 14), 2));
        var cs = drawn.Constrain();
        cs.PointOn(cs.CenterOf(cs.HoleArc(0, 0)), cs.Curve(2));

        var baseline = drawn.Constrain().TrySolve();

        var result = cs.TrySolve();
        Assert.True(result.Converged, result.ToString());
        // ONE new unknown against TWO new rows, so the sketch loses exactly one degree of
        // freedom — which is what "the point lies on the curve" means, and is read off the
        // solver's own rank rather than counted by hand.
        Assert.Equal(baseline.FreeDegreesOfFreedom + 1, result.FreeDegreesOfFreedom);
        Assert.Equal(baseline.ConstrainedDegreesOfFreedom + 2, result.ConstrainedDegreesOfFreedom);
        Assert.Equal(baseline.RemainingDegreesOfFreedom - 1, result.RemainingDegreesOfFreedom);

        // The centre sits on the solved cubic, measured by re-solving the foot from the
        // SOLVED control points — nothing the constraint computed is reused.
        var solved = result.Sketch!;
        Assert.True(DistanceToCubic(solved, SolvedHoleCentre(solved)) < 1e-8);
    }

    /// <summary>
    /// The carrier rides its joints here too: pulling the arch's span wider moves the
    /// bézier, and the constrained point stays on it.
    /// </summary>
    [Fact]
    public void TheBezierCarrierRidesItsJoints()
    {
        var drawn = ArchWithBezier().WithHole(Sketch.Circle(new Vector2d(20, 20), 2));
        var cs = drawn.Constrain();
        cs.PointOn(cs.CenterOf(cs.HoleArc(0, 0)), cs.Curve(2))
          .Fix(cs.Point(0))
          .Horizontal(cs.Line(0))
          .Distance(cs.Point(0), cs.Point(1), 64);

        var result = cs.TrySolve();
        Assert.True(result.Converged, result.ToString());
        var solved = result.Sketch!;
        Assert.Equal(64, (solved.Segments[1].Start - solved.Segments[0].Start).Length, 1e-9);
        Assert.True(DistanceToCubic(solved, SolvedHoleCentre(solved)) < 1e-8);
    }

    /// <summary>A single-segment loop's two joints are ONE variable, so it has no chord for
    /// its shape to ride — refused by name rather than dividing by a zero chord three stages
    /// down.</summary>
    [Fact]
    public void AClosedSingleCurveLoopIsRefusedForABezier()
    {
        var closed = Sketch.Start(0, 0)
            .BezierTo((30, 30), (-30, 30), (0, 0))
            .Close()
            .WithHole(Sketch.Circle(new Vector2d(0, 12), 1));
        var cs = closed.Constrain();
        var thrown = Assert.Throws<ArgumentException>(
            () => cs.PointOn(cs.CenterOf(cs.HoleArc(0, 0)), cs.Curve(0)));
        Assert.Contains("closes on itself", thrown.Message);
    }

    // ---- end tangency ----

    /// <summary>
    /// A bézier's end tangent is a fixed multiple of its chord, so holding it parallel to a
    /// line is the ordinary direction row. Verified on the SOLVED control polygon, whose
    /// leaving direction is 3(C₁ − P₀).
    /// </summary>
    [Fact]
    public void ABeziersEndTangentCanBeHeldParallelToALine()
    {
        var drawn = ArchWithBezier();
        var cs = drawn.Constrain();
        // BOTH base joints are fixed, so the tangency is satisfied by moving the arch's
        // OTHER end — which is what makes the row non-stationary here, where a two-segment
        // loop's would be a constant.
        cs.Fix(cs.Point(0))
          .Fix(cs.Point(1))
          .Tangent(cs.Curve(2), SketchCurveEnd.End, cs.Line(0));

        var result = cs.TrySolve();
        Assert.True(result.Converged, result.ToString());
        var solved = result.Sketch!;

        // Read the two directions off the SOLVED geometry by what they ARE rather than by
        // index: a solve may leave the loop wound the other way, and normalization then
        // reverses both the segment order and each segment's direction.
        var bezier = solved.ToCurves().OfType<BezierCurve2d>().Single();
        var control = bezier.ControlPoints;
        bool endAtOrigin = control[3].Length < control[0].Length;
        var leaving = endAtOrigin ? control[3] - control[2] : control[0] - control[1];
        // The base joins the two FIXED points (0, 0) and (40, 0), so its direction is +X by
        // construction — stating it that way needs no search through the solved segments,
        // whose order and sense are a normalization detail.
        Assert.Equal(0, leaving.Normalized().Cross(Vector2d.UnitX), 1e-9);
    }

    /// <summary>
    /// And the perpendicular form, on an ELLIPTICAL arc — whose end tangent is its own
    /// derivative there, riding the same similarity.
    /// </summary>
    [Fact]
    public void AnEllipticalArcsEndTangentCanBeHeldPerpendicularToALine()
    {
        var drawn = ArchWithEllipse();
        var cs = drawn.Constrain();
        cs.Fix(cs.Point(0))
          .Tangent(cs.Curve(1), SketchCurveEnd.Start, cs.Line(0), perpendicular: true);

        var result = cs.TrySolve();
        Assert.True(result.Converged, result.ToString());
        var solved = result.Sketch!;

        var ellipse = solved.ToCurves().OfType<Ellipse2d>().Single();
        var tangent = ellipse.DerivativeAt(ellipse.Domain.Start).Normalized();
        var line = (solved.Segments[1].Start - solved.Segments[0].Start).Normalized();
        Assert.Equal(0, tangent.Dot(line), 1e-9);
    }

    // ---- helpers ----

    /// <summary>The solved outer loop's elliptical segment, as the exact curve.</summary>
    private static Ellipse2d SolvedEllipse(Sketch solved) =>
        solved.ToCurves().OfType<Ellipse2d>().Single();

    private static Vector2d SolvedHoleCentre(Sketch solved)
    {
        var hole = solved.Holes[0];
        var arc = Assert.IsType<Arc2d>(hole.ToCurves()[0]);
        return arc.Center;
    }

    /// <summary>|M⁻¹(p − C)| for the ellipse's own semi-axis matrix — exactly 1 on it.</summary>
    private static double Normalized(Ellipse2d ellipse, in Vector2d p)
    {
        var (a, b) = (ellipse.SemiAxisX, ellipse.SemiAxisY);
        double determinant = a.X * b.Y - b.X * a.Y;
        var d = p - ellipse.Center;
        return new Vector2d(
            (b.Y * d.X - b.X * d.Y) / determinant,
            (-a.Y * d.X + a.X * d.Y) / determinant).Length;
    }

    private static double DistanceToCubic(Sketch solved, in Vector2d p)
    {
        var bezier = solved.ToCurves().OfType<BezierCurve2d>().Single();
        var control = bezier.ControlPoints;
        double best = double.PositiveInfinity;
        // The carrier is the whole cubic, so the foot legitimately lies outside [0, 1] —
        // the same reason PointOn(point, line) takes the infinite line. The scan only
        // BRACKETS the foot: a distance that is really zero falls off linearly in t, so a
        // scan alone cannot read below |B'| times its own step (~1e-3 here). Newton then
        // lands it, which is what lets the assertion be a real one.
        const int samples = 2000;
        double bestT = 0;
        for (int i = 0; i <= samples; i++)
        {
            double t = -1 + 3.0 * i / samples;
            double distance = (At(control, t) - p).Length;
            if (distance < best)
                (best, bestT) = (distance, t);
        }
        for (int i = 0; i < 40; i++)
        {
            var delta = At(control, bestT) - p;
            var first = Derivative(control, bestT);
            var second = SecondDerivative(control, bestT);
            double gradient = delta.Dot(first);
            double curvature = first.Dot(first) + delta.Dot(second);
            if (!(Math.Abs(curvature) > 0))
                break;
            bestT -= gradient / curvature;
        }
        return (At(control, bestT) - p).Length;
    }

    private static Vector2d At(IReadOnlyList<Vector2d> c, double t)
    {
        double u = 1 - t;
        return c[0] * (u * u * u) + c[1] * (3 * u * u * t) + c[2] * (3 * u * t * t) + c[3] * (t * t * t);
    }

    private static Vector2d Derivative(IReadOnlyList<Vector2d> c, double t)
    {
        double u = 1 - t;
        return (c[1] - c[0]) * (3 * u * u) + (c[2] - c[1]) * (6 * u * t) + (c[3] - c[2]) * (3 * t * t);
    }

    private static Vector2d SecondDerivative(IReadOnlyList<Vector2d> c, double t) =>
        (c[2] - c[1] * 2 + c[0]) * (6 * (1 - t)) + (c[3] - c[2] * 2 + c[1]) * (6 * t);
}
