using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="PolylineCurve3d.Simplified"/> — the tracer-output use of Douglas–Peucker.
/// </summary>
public class PolylineSimplifiedTests
{
    private static Vector3d[] TracedCircle(int samples, double radius = 8)
    {
        var points = new Vector3d[samples];
        for (int i = 0; i < samples; i++)
        {
            double a = 2 * Math.PI * i / samples;
            points[i] = new Vector3d(radius * Math.Cos(a), radius * Math.Sin(a), 3);
        }
        return points;
    }

    [Fact]
    public void ADenselyTracedCircle_KeepsFarFewerVerticesAtAStatedTolerance()
    {
        var traced = new PolylineCurve3d(TracedCircle(600), isClosed: true);
        var simplified = traced.Simplified(1e-2);

        Assert.True(simplified.Points.Count < traced.Points.Count / 4,
            $"{simplified.Points.Count} of {traced.Points.Count}");
        Assert.True(simplified.IsClosed);

        // Every original sample is still within the tolerance of the simplified chain.
        foreach (var p in traced.Points)
        {
            double nearest = double.PositiveInfinity;
            for (int i = 0; i + 1 < simplified.Points.Count; i++)
            {
                var a = simplified.Points[i];
                var b = simplified.Points[i + 1];
                var ab = b - a;
                double t = Math.Clamp((p - a).Dot(ab) / ab.LengthSquared, 0, 1);
                nearest = Math.Min(nearest, (p - (a + ab * t)).Length);
            }
            Assert.True(nearest <= 1e-2 + 1e-12, $"{p} is {nearest} from the simplified chain");
        }
    }

    [Fact]
    public void SimplificationIsIdempotentAndReturnsTheSameInstanceWhenNothingDrops()
    {
        var straight = new PolylineCurve3d([new(0, 0, 0), new(5, 0, 0)]);
        Assert.Same(straight, straight.Simplified(1e-6));

        var traced = new PolylineCurve3d(TracedCircle(200), isClosed: true);
        var once = traced.Simplified(0.01);
        var twice = once.Simplified(0.01);
        Assert.Equal(once.Points.Count, twice.Points.Count);
    }

    [Fact]
    public void TheDomainShortens_BecauseAPolylineIsChordLengthParameterized()
    {
        // The documented consequence: retained POINTS are bit-identical, but parameters
        // into the curve are not preserved, so anything holding them must be rebuilt.
        var traced = new PolylineCurve3d(TracedCircle(400), isClosed: true);
        var simplified = traced.Simplified(0.05);
        Assert.True(simplified.Domain.Length < traced.Domain.Length);
        Assert.Contains(traced.Points, p => p.DistanceTo(simplified.Points[0]) == 0);
    }
}
