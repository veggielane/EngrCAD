using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// STEP export/import of the analytic conic family beyond circles and ellipses —
/// PARABOLA, HYPERBOLA, OFFSET_CURVE_3D — plus <see cref="Parabola3d.ToNurbs"/>. The
/// round-trip fixtures are single-face shells (the reader maps geometry without
/// validating solids), because nothing in <c>SolidFactory</c> produces conic edges yet.
/// </summary>
public class StepConicExportTests
{
    /// <summary>One planar face whose outer loop is <paramref name="curve"/> over
    /// <paramref name="domain"/> closed by the straight chord back to the start.</summary>
    private static BrepSolid FaceWith(Curve3d curve, Interval domain, PlaneSurface plane)
    {
        var start = new BrepVertex(curve.PointAt(domain.Start));
        var end = new BrepVertex(curve.PointAt(domain.End));
        var arc = new BrepEdge(curve, domain, start, end);
        var chordCurve = new Line3d(end.Position, start.Position);
        var chord = new BrepEdge(chordCurve, chordCurve.Domain, end, start);
        var loop = new BrepLoop([new BrepCoedge(arc, sameSense: true), new BrepCoedge(chord, sameSense: true)]);
        return new BrepSolid([new BrepShell([new BrepFace(plane, [loop])])]);
    }

    private static BrepEdge ReadBackEdge<TCurve>(string step)
    {
        var result = StepReader.Read(step);
        var read = Assert.Single(result.Solids);
        return read.Edges.Single(e => e.Curve is TCurve || (e.Curve is CurveSegment s && s.Base is TCurve));
    }

    [Fact]
    public void Parabola_ExportsAsPARABOLA_AndRoundTripsExactly()
    {
        var parabola = new Parabola3d((1, 2, 3), Vector3d.UnitX, Vector3d.UnitY, 0.75, new Interval(-2, 3));
        var solid = FaceWith(parabola, parabola.Domain, new PlaneSurface((1, 2, 3), Vector3d.UnitX, Vector3d.UnitY));

        string step = StepWriter.Write(solid);
        Assert.Contains("PARABOLA(", step);
        Assert.DoesNotContain("B_SPLINE_CURVE", step); // not sampled

        var edge = ReadBackEdge<Parabola3d>(step);
        for (int i = 0; i <= 8; i++)
        {
            double f = i / 8.0;
            var expected = parabola.PointAt(parabola.Domain.ParameterAt(f));
            var actual = edge.Curve.PointAt(edge.Domain.ParameterAt(f));
            Assert.True(expected.DistanceTo(actual) < 1e-12,
                $"parabola point at fraction {f}: {actual} vs {expected}");
        }
    }

    [Fact]
    public void Hyperbola_ExportsAsHYPERBOLA_AndRoundTripsExactly()
    {
        // A tilted branch: semi-axes 2 and 0.8 in a rotated plane.
        var x = new Vector3d(0.6, 0.8, 0);
        var y = new Vector3d(-0.8, 0.6, 0);
        var hyperbola = new Hyperbola3d((0.5, -1, 2), x * 2, y * 0.8, new Interval(-1.2, 1.7));
        var solid = FaceWith(hyperbola, hyperbola.Domain, new PlaneSurface((0.5, -1, 2), x, y));

        string step = StepWriter.Write(solid);
        Assert.Contains("HYPERBOLA(", step);
        Assert.DoesNotContain("B_SPLINE_CURVE", step);

        var edge = ReadBackEdge<Hyperbola3d>(step);
        for (int i = 0; i <= 8; i++)
        {
            double f = i / 8.0;
            var expected = hyperbola.PointAt(hyperbola.Domain.ParameterAt(f));
            var actual = edge.Curve.PointAt(edge.Domain.ParameterAt(f));
            Assert.True(expected.DistanceTo(actual) < 1e-12,
                $"hyperbola point at fraction {f}: {actual} vs {expected}");
        }
    }

    [Fact]
    public void OffsetCurve_ExportsAsOFFSET_CURVE_3D_AndRoundTrips()
    {
        // Offset arc of a circle: base radius 5 offset inward by 1.5 (positive d for a
        // CCW circle offsets inward), trimmed to [0.3, 2.1] by its vertices.
        var circle = new Circle3d((2, 1, 0), Vector3d.UnitX, Vector3d.UnitY, 5);
        var offset = new OffsetCurve3d(circle, Vector3d.UnitZ, 1.5);
        var domain = new Interval(0.3, 2.1);
        var solid = FaceWith(offset, domain, new PlaneSurface((2, 1, 0), Vector3d.UnitX, Vector3d.UnitY));

        string step = StepWriter.Write(solid);
        Assert.Contains("OFFSET_CURVE_3D(", step);
        Assert.Contains("CIRCLE(", step); // the basis stays analytic

        var edge = ReadBackEdge<OffsetCurve3d>(step);
        // Trim parameters come from a Newton solve against the exact offset derivative,
        // so interior points agree far below the seam tier.
        for (int i = 0; i <= 8; i++)
        {
            double f = i / 8.0;
            var expected = offset.PointAt(domain.ParameterAt(f));
            var actual = edge.Curve.PointAt(edge.Domain.ParameterAt(f));
            Assert.True(expected.DistanceTo(actual) < 1e-9,
                $"offset point at fraction {f}: {actual} vs {expected} ({expected.DistanceTo(actual):G3})");
        }
    }

    [Fact]
    public void ParabolaToNurbs_IsTheExactQuadraticBezier()
    {
        var x = new Vector3d(0.6, 0.8, 0);
        var y = new Vector3d(-0.8, 0.6, 0);
        var parabola = new Parabola3d((4, -2, 1), x, y, 0.9, new Interval(-1.5, 2.5));
        var nurbs = parabola.ToNurbs();

        Assert.Equal(2, nurbs.Degree);
        Assert.Equal(3, nurbs.ControlPoints.Count);
        for (int i = 0; i <= 16; i++)
        {
            double s = i / 16.0;
            var expected = parabola.PointAt(parabola.Domain.ParameterAt(s));
            var actual = nurbs.PointAt(nurbs.Domain.ParameterAt(s));
            Assert.True(expected.DistanceTo(actual) < 1e-12,
                $"ToNurbs at fraction {s}: {actual} vs {expected}");
        }
        // Exact end tangent directions too (the middle control point is the tangent
        // intersection).
        Assert.True(nurbs.TangentAt(nurbs.Domain.Start)
            .Cross(parabola.TangentAt(parabola.Domain.Start)).Length < 1e-12);
        Assert.True(nurbs.TangentAt(nurbs.Domain.End)
            .Cross(parabola.TangentAt(parabola.Domain.End)).Length < 1e-12);
    }
}
