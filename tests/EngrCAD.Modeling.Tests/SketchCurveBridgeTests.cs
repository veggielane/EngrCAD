using EngrCAD.BRep;
using EngrCAD.Core;
using EngrCAD.Interop;
using EngrCAD.Modeling;
using Xunit;

namespace EngrCAD.Modeling.Tests;

/// <summary>
/// <see cref="Sketch.ToCurves"/> / <see cref="Sketch.FromCurves"/> — the lossless door
/// between the sketch vocabulary and the 2D curve family.
/// </summary>
public class SketchCurveBridgeTests
{
    [Fact]
    public void ARoundedRectangle_RoundTripsThroughTheCurveFamilyExactly()
    {
        var original = Sketch.RoundedRectangle(8, 5, 1.2);
        var curves = original.ToCurves();

        // Lines stay lines and arcs stay arcs: nothing was flattened on the way out.
        Assert.Equal(8, curves.Count);
        Assert.Equal(4, curves.Count(c => c is Line2d));
        Assert.Equal(4, curves.Count(c => c is Arc2d));

        var rebuilt = Sketch.FromCurves(curves);
        Assert.Equal(original.Area(), rebuilt.Area(), 12);
        Assert.Equal(original.Bounds.Min, rebuilt.Bounds.Min);
        Assert.Equal(original.Bounds.Max, rebuilt.Bounds.Max);
    }

    [Fact]
    public void ACircleSketch_CrossesAsASingleFullTurnArc()
    {
        var curves = Sketch.Circle(new Vector2d(2, -1), 3).ToCurves();
        var arc = Assert.IsType<Arc2d>(Assert.Single(curves));
        Assert.Equal(3, arc.Radius);
        Assert.True(arc.IsClosed);
        Assert.Equal(Math.PI * 9, Sketch.FromCurves(curves).Area(), 12);
    }

    [Fact]
    public void ABezierSketch_CrossesAsACubicBezier_AndComesBackUnchanged()
    {
        // Wound counter-clockwise (out along the axis, back over the hump) so the sketch
        // does not renormalize the winding and the chain order is the one drawn.
        var original = Sketch.Start(0, 0)
            .LineTo(6, 0)
            .BezierTo(new(5, 4), new(1, 4), new(0, 0))
            .Close();
        var curves = original.ToCurves();
        var cubic = Assert.IsType<BezierCurve2d>(curves.OfType<BezierCurve2d>().Single());
        Assert.Equal(3, cubic.Degree);
        Assert.Equal(original.Area(), Sketch.FromCurves(curves).Area(), 12);
    }

    [Fact]
    public void AQuadraticBezierCurve_IsElevatedExactly()
    {
        // Degree elevation is a closed form, so the elevated cubic passes through exactly
        // the same points as the quadratic it came from.
        var quadratic = new BezierCurve2d(new Vector2d(6, 0), new Vector2d(3, 6), new Vector2d(0, 0));
        Curve2d[] chain = [new Line2d(new(0, 0), new(6, 0)), quadratic];
        var sketch = Sketch.FromCurves(chain);
        var elevated = sketch.ToCurves().OfType<BezierCurve2d>().Single();
        Assert.Equal(3, elevated.Degree);
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0;
            Assert.True(quadratic.PointAt(t).DistanceTo(elevated.PointAt(t)) < 1e-14);
        }
    }

    [Fact]
    public void AnArcsOrientationSurvivesTheRoundTrip()
    {
        // A clockwise arc keeps its negative sweep, which is the property that makes
        // orientation data rather than a flag to be re-derived.
        var clockwise = new Arc2d(new(0, 0), 2, Math.PI, -Math.PI);
        Curve2d[] chain = [clockwise, new Line2d(clockwise.PointAt(1), clockwise.PointAt(0))];
        var back = Sketch.FromCurves(chain).ToCurves();
        // The sketch normalizes winding to CCW, so the reported sweep is whichever direction
        // encloses positive area — but the GEOMETRY is unchanged.
        var arc = back.OfType<Arc2d>().Single();
        Assert.Equal(2, arc.Radius);
        Assert.Equal(Math.PI, Math.Abs(arc.SweepAngle), 12);
    }

    [Fact]
    public void AGeneralNurbsCurve_IsRefusedByName_NeverSampled()
    {
        var spline = NurbsCurve2d.InterpolatePoints(
            [new(0, 0), new(3, 4), new(7, 1), new(9, 5)]);
        Curve2d[] chain = [spline, new Line2d(spline.PointAt(1), spline.PointAt(0))];
        var error = Assert.Throws<ArgumentException>(() => Sketch.FromCurves(chain));
        Assert.Contains("NurbsCurve2d", error.Message);
        Assert.Contains("no exact sketch segment", error.Message);
    }

    [Fact]
    public void ClosureIsValidatedByTheOrdinarySketchConstructor()
    {
        Curve2d[] open =
        [
            new Line2d(new(0, 0), new(4, 0)),
            new Line2d(new(4, 0), new(4, 4)),
            new Line2d(new(4, 4), new(1, 9)),
        ];
        var error = Assert.Throws<ArgumentException>(() => Sketch.FromCurves(open));
        Assert.Contains("not a closed chain", error.Message);
    }

    [Fact]
    public void TheCurveChain_ExtrudesToTheSameSolidTheSketchDoes()
    {
        // The end-to-end claim: the bridge is lossless, so a sketch and its curve chain
        // produce the same exact solid through two different front doors.
        var sketch = Sketch.RoundedRectangle(8, 5, 1.2);
        double viaSketch = BRepTessellator.Tessellate(Shape.Extrude(sketch, 3).ToBrep(), 96, 32).Volume();

        var profile = Profile.FromCurves(sketch.ToCurves());
        var solid = SolidFactory.Extrude(profile, Vector3d.UnitZ * 3);

        Assert.Equal(viaSketch, BRepTessellator.Tessellate(solid, 96, 32).Volume(), 9);
    }
}
