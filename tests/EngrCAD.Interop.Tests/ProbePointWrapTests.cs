using EngrCAD.BRep;
using EngrCAD.Core;
using Xunit;

namespace EngrCAD.Interop.Tests;

/// <summary>
/// <c>BrepBoolean.ProbePoint</c> decides "this fragment is a band" by whether its outer loop
/// WRAPS the surface's periodic direction, and the band path then probes halfway toward the
/// surface's own v domain edge. A loop that merely SPANS most of the period without wrapping
/// — a contractible facet, of which a threaded rod's end-chamfer facet (272°) is the case that
/// found this — sends the probe clean outside the fragment, and the boolean classifies the
/// facet away. The decision is net u drift; see <c>FaceGeometry.LoopWrapsPeriod</c>.
/// </summary>
public class ProbePointWrapTests
{
    /// <summary>
    /// A contractible facet spanning <paramref name="degrees"/> of a closed extruded band,
    /// confined to v ∈ [0.25, 0.5] so a probe walking toward a v domain edge leaves it.
    /// </summary>
    private static BrepFace Facet(double degrees)
    {
        const double radius = 10, height = 20;
        double span = degrees * Math.PI / 180;
        var generator = new Circle3d(Vector3d.Zero, Vector3d.UnitX, Vector3d.UnitY, radius);
        var surface = new ExtrudedSurface(generator, new Vector3d(0, 0, height));

        Circle3d RingAt(double v) =>
            new(new Vector3d(0, 0, height * v), Vector3d.UnitX, Vector3d.UnitY, radius);
        var bottom = RingAt(0.25);
        var top = RingAt(0.5);

        var a = new BrepVertex(surface.PointAt(0, 0.25));
        var b = new BrepVertex(surface.PointAt(span, 0.25));
        var c = new BrepVertex(surface.PointAt(span, 0.5));
        var d = new BrepVertex(surface.PointAt(0, 0.5));

        var bottomEdge = new BrepEdge(bottom, new Interval(0, span), a, b);
        var rightEdge = new BrepEdge(new Line3d(b.Position, c.Position), Interval.Unit, b, c);
        var topEdge = new BrepEdge(top, new Interval(0, span), d, c);
        var leftEdge = new BrepEdge(new Line3d(d.Position, a.Position), Interval.Unit, d, a);

        var loop = new BrepLoop(
        [
            new BrepCoedge(bottomEdge, true),
            new BrepCoedge(rightEdge, true),
            new BrepCoedge(topEdge, false),
            new BrepCoedge(leftEdge, true),
        ]);
        return new BrepFace(surface, [loop], isReversed: false);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(200)]
    [InlineData(272)]   // the measured chamfer-facet span
    [InlineData(350)]
    public void ContractibleFacet_IsProbedInsideItself(double degrees)
    {
        var face = Facet(degrees);

        // The fixture must actually carry the configuration: one loop, spanning more than
        // three quarters of the period once past 270 degrees, and NOT wrapping.
        var pulled = FaceGeometry.PullLoops(face)[0];
        double period = FaceGeometry.PeriodU(face.Surface);
        Assert.Equal(degrees > 270, pulled.Max(p => p.X) - pulled.Min(p => p.X) > 0.75 * period);
        Assert.False(FaceGeometry.LoopWrapsPeriod(pulled, period));

        var probe = BrepBoolean.ProbePoint(face);
        Assert.True(FaceGeometry.Contains(face, probe),
            $"probe {probe} is not inside the {degrees}-degree facet");

        // ...and specifically it stays inside the facet's own v band, where the band path
        // (halfway to the surface's v domain edge, i.e. v = 0.6875) would not.
        Assert.True(face.Surface.TryProjectPoint(probe, out var uv, FaceGeometry.InverseEvaluationTolerance));
        Assert.InRange(uv.Y, 0.25, 0.5);
    }
}
