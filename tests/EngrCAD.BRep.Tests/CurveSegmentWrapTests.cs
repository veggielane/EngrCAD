using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.BRep.Tests;

/// <summary>
/// <see cref="CurveSegment"/>'s parameter mapping at the edges of its base's domain.
/// <para>The wrap is only meaningful on a CLOSED base — a segment straddling a circle's
/// seam legitimately runs past the domain end, and wrapping IS what that parameter means.
/// On an OPEN base there is nothing on the other side, and the clipping arithmetic that
/// builds these segments routinely overshoots by an ULP: the old unconditional wrap then
/// did not nudge the sample, it TELEPORTED it to the base's start. Measured on a thread's
/// end chamfer, one such sample came back 0.375 mm away at the far end of a conical spiral
/// — off the face entirely — after which the trimmed band tier stopped recognizing a band,
/// the ear clipper ran and folded 244 of 3562 facets.</para>
/// </summary>
public class CurveSegmentWrapTests
{
    [Fact]
    public void AnOpenBaseClampsAParameterAnUlpPastItsDomain()
    {
        var line = new Line3d((0, 0, 0), (10, 0, 0));
        double end = line.Domain.End;
        var segment = new CurveSegment(line, line.Domain.Start, Math.BitIncrement(end));

        Assert.Equal(line.PointAt(end).X, segment.PointAt(1).X, 12);
        // The whole point: without the clamp this landed on the base's START.
        Assert.True(segment.PointAt(1).DistanceTo(line.PointAt(line.Domain.Start)) > 1,
            "a one-ulp overshoot must not teleport the sample to the other end");
    }

    [Fact]
    public void AnOpenBaseClampsBelowItsDomainToo()
    {
        var line = new Line3d((0, 0, 0), (10, 0, 0));
        double start = line.Domain.Start;
        var segment = new CurveSegment(line, Math.BitDecrement(start), line.Domain.End);
        Assert.Equal(line.PointAt(start).X, segment.PointAt(0).X, 12);
    }

    [Fact]
    public void AClosedBaseStillWrapsAcrossItsSeam()
    {
        // A quarter arc straddling a circle's seam: parameters past the domain end are
        // the whole reason the wrap exists, and they must keep working.
        var circle = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, 2);
        double period = circle.Domain.Length;
        var segment = new CurveSegment(
            circle, circle.Domain.End - period / 8, circle.Domain.End + period / 8);

        Assert.Equal(circle.PointAt(circle.Domain.Start).X, segment.PointAt(0.5).X, 9);
        Assert.Equal(circle.PointAt(circle.Domain.Start).Y, segment.PointAt(0.5).Y, 9);
        for (int i = 0; i <= 16; i++)
        {
            var p = segment.PointAt(i / 16.0);
            Assert.Equal(2.0, Math.Sqrt(p.X * p.X + p.Y * p.Y), 12);
        }
    }
}
