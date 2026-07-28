using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="Curve2d.ToCurve3d"/> and <see cref="Profile.FromCurves"/> — the exact bridge
/// from the sketch-plane curve vocabulary into the topology one. Every conversion here is a
/// re-expression, so the test is always "the same points, at the same parameters".
/// </summary>
public class Curve2dBridgeTests
{
    /// <summary>A tilted, translated plane, so a conversion that quietly assumed world XY
    /// cannot pass.</summary>
    private static Frame3d TiltedPlane() =>
        Frame3d.FromXY(new Vector3d(7, -2, 3), new Vector3d(1, 1, 0), new Vector3d(-1, 1, 2));

    private static void SamplesAgree(Curve2d flat, Curve3d lifted, in Frame3d plane, int samples = 17)
    {
        for (int i = 0; i <= samples; i++)
        {
            double t = (double)i / samples;
            var expected = plane.ToWorld(new Vector3d(
                flat.PointAt(flat.Domain.ParameterAt(t)).X,
                flat.PointAt(flat.Domain.ParameterAt(t)).Y, 0));
            var actual = lifted.PointAt(lifted.Domain.ParameterAt(t));
            Assert.True(expected.DistanceTo(actual) < 1e-12,
                $"t={t}: {expected} vs {actual}");
        }
    }

    [Fact]
    public void Line_LiftsExactly()
    {
        var plane = TiltedPlane();
        var line = new Line2d(new(1, 2), new(6, -3));
        var lifted = line.ToCurve3d(plane);
        Assert.IsType<Line3d>(lifted);
        SamplesAgree(line, lifted, plane);
    }

    [Fact]
    public void PartialArc_LiftsToATrimmedCircle_AtTheSamePoints()
    {
        var plane = TiltedPlane();
        var arc = new Arc2d(new(2, 1), 4, 0.3, 1.9);
        var lifted = arc.ToCurve3d(plane);
        Assert.IsType<Circle3d>(lifted.Underlying); // still classifiable downstream
        SamplesAgree(arc, lifted, plane);
    }

    [Fact]
    public void ANegativeSweep_CrossesAsADecreasingParameterRange_NotAReversal()
    {
        // The point of Arc2d's signed sweep: orientation is data, so the lifted curve's own
        // parameter direction already matches and nothing needs a reverse-and-hope repair.
        var plane = TiltedPlane();
        var clockwise = new Arc2d(new(0, 0), 3, 1.0, -1.4);
        var lifted = clockwise.ToCurve3d(plane);
        var segment = Assert.IsType<CurveSegment>(lifted);
        Assert.True(segment.BaseEnd < segment.BaseStart);
        SamplesAgree(clockwise, lifted, plane);
    }

    [Fact]
    public void AFullTurnArc_LiftsToAClosedCircleWhoseParameterFollowsTheSweep()
    {
        var plane = TiltedPlane();
        foreach (double sweep in new[] { 2 * Math.PI, -2 * Math.PI })
        {
            var full = new Arc2d(new(1, -1), 2.5, 0.7, sweep);
            var lifted = full.ToCurve3d(plane);
            Assert.IsType<Circle3d>(lifted);
            Assert.True(lifted.IsClosed);
            SamplesAgree(full, lifted, plane);
        }
    }

    [Fact]
    public void Bezier_LiftsToTheEquivalentBezierKnotNurbs()
    {
        var plane = TiltedPlane();
        var bezier = new BezierCurve2d(new(0, 0), new(1, 4), new(5, 4), new(6, 0));
        var lifted = Assert.IsType<NurbsCurve>(bezier.ToCurve3d(plane));
        Assert.Equal(3, lifted.Degree);
        Assert.Equal(4, lifted.ControlPoints.Count);
        SamplesAgree(bezier, lifted, plane);
    }

    [Fact]
    public void QuadraticBezier_LiftsWithoutElevation()
    {
        var plane = TiltedPlane();
        var quadratic = new BezierCurve2d(new(0, 0), new(2, 3), new(4, 0));
        var lifted = Assert.IsType<NurbsCurve>(quadratic.ToCurve3d(plane));
        Assert.Equal(2, lifted.Degree);
        SamplesAgree(quadratic, lifted, plane);
    }

    [Fact]
    public void RationalNurbs_KeepsItsWeights_SoAnExactArcStaysExact()
    {
        var plane = TiltedPlane();
        var arc = NurbsCurve2d.Arc(new(0, 0), 5, 0.2, 1.6);
        var lifted = Assert.IsType<NurbsCurve>(arc.ToCurve3d(plane));
        Assert.Equal(arc.Weights, lifted.Weights);
        SamplesAgree(arc, lifted, plane);

        // Still an exact circle in 3D: every sample sits at radius 5 from the lifted centre.
        var centre = plane.ToWorld(Vector3d.Zero);
        for (int i = 0; i <= 12; i++)
        {
            var p = lifted.PointAt(lifted.Domain.ParameterAt((double)i / 12));
            Assert.Equal(5.0, centre.DistanceTo(p), 12);
        }
    }

    // ---- Profile.FromCurves ----

    [Fact]
    public void Profile_FromCurves_KeepsArcsExact()
    {
        // A stadium outline: two lines and two semicircular arcs. Through Region2d this
        // would arrive as a polygon; through the curve family the arcs survive.
        double half = 5, r = 2;
        Curve2d[] chain =
        [
            new Line2d(new(-half, -r), new(half, -r)),
            new Arc2d(new(half, 0), r, -Math.PI / 2, Math.PI),
            new Line2d(new(half, r), new(-half, r)),
            new Arc2d(new(-half, 0), r, Math.PI / 2, Math.PI),
        ];
        var profile = Profile.FromCurves(chain, TiltedPlane());
        Assert.Equal(4, profile.Segments.Count);
        Assert.IsType<Line3d>(profile.Segments[0]);
        Assert.IsType<Circle3d>(profile.Segments[1].Underlying);
    }

    [Fact]
    public void Profile_FromCurves_UsesTheOrdinaryConstructorsValidation()
    {
        // An open chain must be refused by the SAME check every other profile goes through,
        // with the same message — there is no second copy of closure validation.
        Curve2d[] open =
        [
            new Line2d(new(0, 0), new(4, 0)),
            new Line2d(new(4, 0), new(4, 4)),
            new Line2d(new(4, 4), new(1, 9)),
        ];
        var error = Assert.Throws<ArgumentException>(() => Profile.FromCurves(open));
        Assert.Contains("not a closed chain", error.Message);
    }

    [Fact]
    public void Profile_FromCurves_DefaultsToTheWorldXyPlane()
    {
        Curve2d[] square =
        [
            new Line2d(new(0, 0), new(4, 0)),
            new Line2d(new(4, 0), new(4, 4)),
            new Line2d(new(4, 4), new(0, 4)),
            new Line2d(new(0, 4), new(0, 0)),
        ];
        var profile = Profile.FromCurves(square);
        Assert.True(profile.Normal.AreEqual(Vector3d.UnitZ, Tolerance.Default));
    }
}
