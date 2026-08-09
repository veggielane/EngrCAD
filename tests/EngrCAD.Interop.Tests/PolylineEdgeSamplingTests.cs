using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// The tessellator samples a marching-tracer polyline at its VERTICES, because that is
/// the only place it lies on its carrier surface. It used to test for the raw
/// <c>PolylineCurve3d</c> only — so an edge whose curve is a <c>CurveSegment</c>
/// WRAPPING one (what the face splitter hands back after a cut) fell through to the
/// uniform path, putting every interior sample a sagitta off the surface. Both cases now
/// go through <c>FaceGeometry.ExactSampleParameters</c>, the single rule the pullback and
/// the tightest-turn probes already used.
/// </summary>
public class PolylineEdgeSamplingTests
{
    /// <summary>A quarter of a unit circle in the z = 0 plane, as the tracer delivers it.</summary>
    private static PolylineCurve3d ArcPolyline(int segments = 12)
    {
        var points = new List<Vector3d>();
        for (int i = 0; i <= segments; i++)
        {
            double a = Math.PI / 2 * i / segments;
            points.Add(new Vector3d(Math.Cos(a), Math.Sin(a), 0));
        }
        return new PolylineCurve3d(points);
    }

    private static BrepEdge EdgeOver(Curve3d curve) =>
        new(curve, curve.Domain,
            new BrepVertex(curve.PointAt(curve.Domain.Start)),
            new BrepVertex(curve.PointAt(curve.Domain.End)));

    [Fact]
    public void ASegmentOfATracerPolylineSamplesAtItsVertices()
    {
        var polyline = ArcPolyline();
        var segment = new CurveSegment(
            polyline, polyline.Domain.ParameterAt(0.25), polyline.Domain.ParameterAt(0.75));

        var samples = BRepTessellator.SampleEdge(EdgeOver(segment), segmentsPerCircle: 32, curveSamples: 24);

        // Every sample sits exactly on the circle the polyline was traced from — which
        // the polyline touches only at its vertices.
        Assert.True(samples.Count > 2);
        foreach (var p in samples)
            Assert.Equal(1.0, p.Length, 12);

        // Endpoints are the segment's own, exactly.
        Assert.Equal(0, samples[0].DistanceTo(segment.PointAt(0)), 12);
        Assert.Equal(0, samples[^1].DistanceTo(segment.PointAt(1)), 12);
    }

    [Fact]
    public void TheUniformPathWouldHaveMissedTheSurface()
    {
        // The bug this pins, measured: sampling the same segment uniformly puts interior
        // points well past the 1e-6 inverse-evaluation tolerance, which is exactly how a
        // pullback silently returns no runs and a band gets whole-classified away.
        var polyline = ArcPolyline();
        var segment = new CurveSegment(
            polyline, polyline.Domain.ParameterAt(0.25), polyline.Domain.ParameterAt(0.75));

        double worst = 0;
        for (int i = 1; i < 24; i++)
            worst = Math.Max(worst, Math.Abs(segment.PointAt(i / 24.0).Length - 1));
        Assert.True(worst > FaceGeometry.InverseEvaluationTolerance,
            $"uniform sampling deviates by {worst:E3}, which must exceed the pullback tolerance");
    }

    [Fact]
    public void ARawTracerPolylineStillSamplesAtItsVertices()
    {
        // The pre-existing case, unchanged by the reroute.
        var polyline = ArcPolyline(6);
        var samples = BRepTessellator.SampleEdge(EdgeOver(polyline), segmentsPerCircle: 32, curveSamples: 24);

        Assert.Equal(7, samples.Count);
        for (int i = 0; i < samples.Count; i++)
            Assert.Equal(0, samples[i].DistanceTo(polyline.Points[i]), 12);
    }

    [Fact]
    public void AnAnalyticEdgeIsUnaffected()
    {
        var line = new Line3d((0, 0, 0), (3, 0, 0));
        var samples = BRepTessellator.SampleEdge(EdgeOver(line), segmentsPerCircle: 32, curveSamples: 24);
        Assert.Equal(2, samples.Count); // straight edges stay two points

        // An OPEN edge over an angular curve takes the LARGER of the two counts. This one
        // spans the circle's whole domain, so the angular rule asks for segmentsPerCircle
        // segments (33 points) where curveSamples alone would give 25 — the floor that
        // used to make a split rim coarser than its unsplit twin at high density.
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 2);
        var arcSamples = BRepTessellator.SampleEdge(EdgeOver(circle), 32, 24);
        Assert.Equal(33, arcSamples.Count);
        foreach (var p in arcSamples)
            Assert.Equal(2.0, p.Length, 12);

        // A SHORT arc keeps curveSamples, which is the monotone half of the rule: at the
        // default density curveSamples is the finer of the two for anything under about a
        // half turn, so nothing in the repository is sampled more coarsely than it was.
        var quarter = EdgeOver(new CurveSegment(circle, 0, Math.PI / 2));
        Assert.Equal(25, BRepTessellator.SampleEdge(quarter, 32, 24).Count);

        // And the defect the rule removes is a FLOOR rather than a coarseness: without the
        // angular half a split rim carried the SAME count at every density, so raising
        // segmentsPerCircle refined the grid around it and never the rim itself.
        Assert.Equal(25, BRepTessellator.SampleEdge(quarter, 32, 24).Count);
        Assert.Equal(33, BRepTessellator.SampleEdge(quarter, 128, 24).Count);
        Assert.Equal(65, BRepTessellator.SampleEdge(quarter, 256, 24).Count);
    }
}
