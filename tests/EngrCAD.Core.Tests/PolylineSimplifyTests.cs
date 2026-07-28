using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Core.Tests;

/// <summary>
/// Douglas–Peucker simplification: the tolerance-driven companion to
/// <c>Region2d.WithoutCollinearVertices</c>' exact pass.
/// </summary>
public class PolylineSimplifyTests
{
    /// <summary>Points on a unit circle arc, sampled far more densely than its shape needs.</summary>
    private static Vector2d[] DenseArc(int count, double radius = 10, double sweep = Math.PI)
    {
        var points = new Vector2d[count];
        for (int i = 0; i < count; i++)
        {
            double a = sweep * i / (count - 1);
            points[i] = new Vector2d(radius * Math.Cos(a), radius * Math.Sin(a));
        }
        return points;
    }

    [Fact]
    public void ExactlyCollinearRun_CollapsesToItsEndpoints()
    {
        Vector2d[] line = [new(0, 0), new(1, 0), new(2, 0), new(3, 0), new(4, 0)];
        var simplified = PolylineSimplify.Simplify(line, 1e-9);
        Assert.Equal(2, simplified.Count);
        Assert.Equal(new Vector2d(0, 0), simplified[0]);
        Assert.Equal(new Vector2d(4, 0), simplified[1]);
    }

    [Fact]
    public void EveryDroppedVertex_StaysWithinTheTolerance()
    {
        var arc = DenseArc(400);
        foreach (double tolerance in new[] { 1e-3, 1e-2, 0.1, 1.0 })
        {
            var simplified = PolylineSimplify.Simplify(arc, tolerance);
            double deviation = PolylineSimplify.MaxDeviation(arc, simplified);
            Assert.True(deviation <= tolerance,
                $"tolerance {tolerance}: deviation {deviation} over {simplified.Count} points");
            Assert.True(simplified.Count < arc.Length);
        }
    }

    [Fact]
    public void LooserTolerances_NeverKeepMorePoints()
    {
        var arc = DenseArc(400);
        int previous = int.MaxValue;
        foreach (double tolerance in new[] { 1e-4, 1e-3, 1e-2, 0.1, 1.0, 4.0 })
        {
            int count = PolylineSimplify.Simplify(arc, tolerance).Count;
            Assert.True(count <= previous, $"tolerance {tolerance} kept {count} > {previous}");
            previous = count;
        }
    }

    [Fact]
    public void EndpointsAreAlwaysKept_AndRetainedPointsAreBitIdentical()
    {
        var arc = DenseArc(200);
        var simplified = PolylineSimplify.Simplify(arc, 0.5);
        Assert.Equal(arc[0], simplified[0]);
        Assert.Equal(arc[^1], simplified[^1]);
        // Every output point is one of the inputs, unmoved.
        foreach (var p in simplified)
            Assert.Contains(arc, q => q.X == p.X && q.Y == p.Y);
    }

    [Fact]
    public void IsDeterministic()
    {
        var arc = DenseArc(300);
        var first = PolylineSimplify.Simplify(arc, 0.05);
        for (int run = 0; run < 3; run++)
        {
            var again = PolylineSimplify.Simplify(arc, 0.05);
            Assert.Equal(first.Count, again.Count);
            for (int i = 0; i < first.Count; i++)
                Assert.Equal(first[i], again[i]);
        }
    }

    [Fact]
    public void ANonPositiveTolerance_ReturnsTheInputUnchanged()
    {
        var arc = DenseArc(50);
        Assert.Same(arc, PolylineSimplify.Simplify(arc, 0));
        Assert.Same(arc, PolylineSimplify.Simplify(arc, -1));
    }

    [Fact]
    public void ADoubledBackChord_MeasuresToTheSegment_NotItsInfiniteLine()
    {
        // A spike that returns along its own line: the tip is 5 away from the SEGMENT
        // (0,0)-(1,0) endpoint, but exactly 0 from that segment's infinite extension. Using
        // the line would delete the spike at any tolerance above zero.
        Vector2d[] spike = [new(0, 0), new(6, 0), new(1, 0)];
        var simplified = PolylineSimplify.Simplify(spike, 1.0);
        Assert.Equal(3, simplified.Count);
    }

    [Fact]
    public void ClosedLoop_KeepsAtLeastThreePoints_EvenAtAbsurdTolerance()
    {
        var circle = new Vector2d[64];
        for (int i = 0; i < 64; i++)
        {
            double a = 2 * Math.PI * i / 64;
            circle[i] = new Vector2d(5 * Math.Cos(a), 5 * Math.Sin(a));
        }
        var simplified = PolylineSimplify.SimplifyLoop(circle, 1000);
        Assert.True(simplified.Count >= 3);
    }

    [Fact]
    public void ClosedLoop_KeepsTheCornersOfAPolygonWithNoisySides()
    {
        // A square whose edges carry 20 intermediate samples each, none more than 1e-6 off
        // the edge: the four corners must survive and nothing else should.
        var loop = new List<Vector2d>();
        Vector2d[] corners = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        for (int c = 0; c < 4; c++)
        {
            var a = corners[c];
            var b = corners[(c + 1) % 4];
            for (int i = 0; i < 20; i++)
            {
                double t = (double)i / 20;
                var offset = new Vector2d(0, 1e-6 * Math.Sin(20 * Math.PI * t));
                loop.Add(a + (b - a) * t + offset);
            }
        }
        var simplified = PolylineSimplify.SimplifyLoop(loop, 1e-3);
        Assert.Equal(4, simplified.Count);
    }

    [Fact]
    public void ClosedLoopDeviation_IsMeasuredAroundTheClosingChordToo()
    {
        var circle = new Vector2d[128];
        for (int i = 0; i < 128; i++)
        {
            double a = 2 * Math.PI * i / 128;
            circle[i] = new Vector2d(7 * Math.Cos(a), 7 * Math.Sin(a));
        }
        var simplified = PolylineSimplify.SimplifyLoop(circle, 0.05);
        Assert.True(PolylineSimplify.MaxDeviation(circle, simplified, closed: true) <= 0.05);
    }

    [Fact]
    public void ThreeDimensionalCurves_ObeyTheSameContract()
    {
        // A helix arc sampled 500 times: the 3D path must respect the tolerance and keep
        // its endpoints, exactly as the 2D one does.
        var helix = new Vector3d[500];
        for (int i = 0; i < 500; i++)
        {
            double t = 4 * Math.PI * i / 499;
            helix[i] = new Vector3d(3 * Math.Cos(t), 3 * Math.Sin(t), 0.5 * t);
        }
        var simplified = PolylineSimplify.Simplify(helix, 0.01);
        Assert.True(simplified.Count < 500);
        Assert.Equal(helix[0], simplified[0]);
        Assert.Equal(helix[^1], simplified[^1]);
        Assert.True(PolylineSimplify.MaxDeviation(helix, simplified) <= 0.01);
    }
}
